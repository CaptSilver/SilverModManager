using Microsoft.Extensions.Logging;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

public partial class ALoadoutSynchronizer
{
    /// <summary>
    /// What the operations that exist to take mods out of the folder do not need a question for: the player asked for
    /// the removal, and the reset or delete that follows removes the same files.
    /// </summary>
    private static bool RemovalWasAskedFor(SyncTrigger? trigger)
        => trigger is SyncTrigger.ResetGameFolder or SyncTrigger.SwitchLoadout or SyncTrigger.Unmanage or SyncTrigger.DeleteLoadout;

    /// <summary>
    /// Counts what the tree takes out of the folder against what the loadout had deployed. Only a file the last apply
    /// deployed and the loadout no longer provides counts, so files the app backs up or replaces are not removals.
    /// </summary>
    internal static (List<GamePath> Removed, int Deployed) MeasureRemoval(Dictionary<GamePath, SyncNode> tree)
    {
        List<GamePath> removed = [];
        var kept = 0;
        foreach (var (path, node) in tree)
        {
            if (!node.HavePrevious) continue;

            if (node.Actions.HasFlag(Actions.DeleteFromDisk) && !node.Actions.HasFlag(Actions.ExtractToDisk) && !node.HaveLoadout)
                removed.Add(path);
            else if (node.HaveLoadout && node.SourceItemType != LoadoutSourceItemType.Game)
                kept++;
        }

        return (removed, removed.Count + kept);
    }

    /// <summary>
    /// Stops an apply that would take a large part of the loadout's deployed files out of the folder unless the player
    /// says yes. Runs before anything is written, so a no leaves the folder, the disk state and the loadout as they were.
    /// </summary>
    private async Task ConfirmMassRemoval(Dictionary<GamePath, SyncNode> tree, Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        if (RemovalWasAskedFor(SyncOrigin.Current)) return;

        var (removed, deployed) = MeasureRemoval(tree);
        if (!_massRemovalThreshold.Applies(removed.Count, deployed)) return;

        var installation = loadout.InstallationInstance;
        var (reasons, unexplained) = SwitchedOffReasons(loadout, removed);
        var removal = new MassRemoval(removed.Count, deployed, reasons, unexplained);

        switch (MassRemovalConsent.Current)
        {
            case true:
                Logger.LogInformation("Removing {Count} of {Deployed} deployed files from loadout {Loadout}: confirmed in advance", removed.Count, deployed, loadout.Name);
                return;
            case false:
                Logger.LogInformation("Not removing {Count} of {Deployed} deployed files from loadout {Loadout}: the caller cannot be asked and did not confirm", removed.Count, deployed, loadout.Name);
                throw new MassRemovalException(installation, removal);
        }

        Logger.LogInformation("Asking before removing {Count} of {Deployed} deployed files from loadout {Loadout}: {Reasons}", removed.Count, deployed, loadout.Name,
            reasons.Count == 0 ? "no switched-off item explains them" : string.Join(", ", reasons.Select(static r => $"{r.Name} ({r.FileCount})")));

        var confirmed = await _massRemovalPrompt.Confirm(installation, removal, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!confirmed) throw new MassRemovalException(installation, removal) { Declined = true };

        Logger.LogInformation("Removing {Count} files from loadout {Loadout}: confirmed", removed.Count, loadout.Name);
    }

    /// <summary>
    /// Names the switched-off item each removed file belonged to: the outermost disabled item above the file's row, so
    /// a switched-off collection is named rather than each of its mods. A file with no such item is unexplained.
    /// </summary>
    private static (List<MassRemovalReason> Reasons, int Unexplained) SwitchedOffReasons(Loadout.ReadOnly loadout, List<GamePath> removed)
    {
        var wanted = removed.ToHashSet();
        var explained = new HashSet<GamePath>();
        var owners = new Dictionary<EntityId, (string Name, int Count)>();

        foreach (var file in LoadoutItem.FindByLoadout(loadout.Db, loadout.LoadoutId).OfTypeLoadoutItemWithTargetPath())
        {
            var path = (GamePath)file.TargetPath;
            if (!wanted.Contains(path) || explained.Contains(path)) continue;

            var owner = OutermostDisabled(file.AsLoadoutItem());
            if (owner is not { } disabled) continue;

            explained.Add(path);
            owners[disabled.Id] = owners.TryGetValue(disabled.Id, out var seen) ? (seen.Name, seen.Count + 1) : (disabled.Name, 1);
        }

        var reasons = owners.Values
            .OrderByDescending(static o => o.Count).ThenBy(static o => o.Name, StringComparer.Ordinal)
            .Select(static o => new MassRemovalReason(o.Name, o.Count))
            .ToList();
        return (reasons, removed.Count - explained.Count);
    }

    private static LoadoutItem.ReadOnly? OutermostDisabled(LoadoutItem.ReadOnly item)
    {
        LoadoutItem.ReadOnly? found = null;
        var current = item;
        while (true)
        {
            if (current.IsDisabled) found = current;
            if (!current.HasParent()) return found;
            current = current.Parent.AsLoadoutItem();
        }
    }
}
