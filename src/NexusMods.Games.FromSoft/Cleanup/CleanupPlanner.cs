using NexusMods.Games.FromSoft.Me3;
using NexusMods.Platform.Proton.Steam;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.FromSoft.Cleanup;

/// <summary>Steam's launch options line and what would replace it.</summary>
public sealed record LaunchOptionChange(string OldLine, string NewLine);

/// <param name="GameFolder">The absolute path of the <c>Game</c> folder, for the commands shown to the user.</param>
/// <param name="Inventory">Everything in the <c>Game</c> folder, see <see cref="GameFolderInventory.Take"/>.</param>
/// <param name="Baseline">
/// The paths the game's own depots list, relative to the <c>Game</c> folder; null when the app has no file list for
/// the installed build, in which case nothing can be called unrecognised.
/// </param>
/// <param name="LauncherImmutable">Whether the launcher carries the immutable attribute.</param>
/// <param name="LaunchOption">Set when Steam's launch options for the game start another manager.</param>
/// <param name="UpdatePending">Whether Steam has an update for the game it has not applied yet.</param>
public sealed record CleanupInput(
    string GameFolder,
    IReadOnlyList<InventoryEntry> Inventory,
    IReadOnlySet<string>? Baseline,
    bool LauncherImmutable,
    LaunchOptionChange? LaunchOption,
    bool UpdatePending);

/// <summary>
/// Works out the steps that turn a game folder left by another mod manager back into a plain Steam install. Reads
/// nothing itself: the folder comes in as an inventory, so every case is a list of entries in a test.
/// </summary>
public static class CleanupPlanner
{
    // Proton rewrites this cache on every run, and the game's own file list does not know it.
    private const string ProtonShaderCache = "vkd3d-proton.cache";

    public static CleanupPlan Build(CleanupInput input, CleanupRules rules, FromSoftGameLayout layout)
    {
        var folder = new Folder(input);
        var steps = new List<CleanupStep>();

        steps.AddRange(LauncherSteps(folder, input, rules));
        steps.AddRange(ModSteps(folder, rules));
        steps.AddRange(LinkSteps(folder));
        steps.AddRange(LeftoverSteps(folder, rules));
        steps.AddRange(ReviewSteps(folder, layout));
        if (input.Baseline is null)
            steps.Add(new CleanupStep(CleanupStepKind.Advice, "The app has no file list for the installed build, so files that are not part of the game cannot be told apart and none are listed for review."));

        if (input.LaunchOption is { } change)
        {
            steps.Add(new CleanupStep(
                CleanupStepKind.ReplaceLaunchOption,
                $"Replace Steam's launch options '{change.OldLine}' with '{change.NewLine}'. Steam must be closed to write them; while the old line stays, Steam's Play runs the other manager without any of the app's checks.",
                change.OldLine,
                change.NewLine));
        }

        if (input.UpdatePending)
            steps.Add(new CleanupStep(CleanupStepKind.Advice, "Steam has an update for the game it has not applied. Once the steps above are done, let Steam finish it."));

        return new CleanupPlan([..steps]);
    }

    private static IEnumerable<CleanupStep> LauncherSteps(Folder folder, CleanupInput input, CleanupRules rules)
    {
        if (rules.Launcher is not { } launcherName) yield break;

        var launcher = folder.Find(launcherName);
        if (launcher is not null && input.LauncherImmutable)
        {
            // The app never asks for root; the user runs this and the plan is built again.
            yield return new CleanupStep(
                CleanupStepKind.UserCommand,
                $"Run this yourself, the app never uses sudo: sudo chattr -i {SteamLaunchOptionLine.ShellQuote($"{input.GameFolder}/{launcher.Path}")}",
                launcher.Path);
        }

        // Whatever the app's file list says of it, the launcher is the game's own, and a restored one must not be
        // flagged for review just because the list was built while it was still swapped.
        if (launcher is not null) folder.Handle(launcher.Path);
        if (folder.Find(launcherName + rules.LauncherBackupSuffix) is not { Kind: InventoryKind.File } backup) yield break;

        // The backup is the real launcher, so it goes back even when the swapped one beside it is already gone.
        folder.Handle(backup.Path);
        var target = launcher?.Path ?? launcherName;
        yield return new CleanupStep(
            CleanupStepKind.Move,
            launcher is null ? $"Move {backup.Path} to {target}" : $"Move {backup.Path} over {target}",
            backup.Path,
            target,
            launcher is not null && input.LauncherImmutable ? $"{target} still has the immutable attribute" : null);
    }

    private static IEnumerable<CleanupStep> ModSteps(Folder folder, CleanupRules rules)
    {
        if (rules.ModsFolder is not { } modsName) yield break;

        var modFolders = folder.Entries
            .Where(e => e.Kind == InventoryKind.Directory && Segments(e.Path) is [var root, _] && root.Equals(modsName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var mod in modFolders)
        {
            var inside = folder.Under(mod.Path).Where(e => e.Kind != InventoryKind.Directory).ToArray();
            var configs = inside.Where(e => e.Kind == InventoryKind.File && e.Path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)).ToArray();

            foreach (var config in configs)
            {
                var overlayPath = Me3ModsLayout.OverlayPath(config.Path);
                yield return new CleanupStep(CleanupStepKind.Copy, $"Copy {config.Path} into the loadout as {overlayPath}", config.Path, overlayPath);
            }

            folder.Handle(mod.Path);
            yield return new CleanupStep(
                CleanupStepKind.Delete,
                $"Delete {mod.Path} ({Count(inside.Length, "file")}{(configs.Length > 0 ? ", the .ini files are copied first" : "")})",
                mod.Path);
        }

        // An emptied mods folder would be the only thing left of the old layout, and a second plan would list it.
        if (folder.Find(modsName) is { Kind: InventoryKind.Directory } mods && modFolders.Length > 0 && folder.Under(mods.Path).All(folder.IsHandled))
        {
            folder.Handle(mods.Path);
            yield return new CleanupStep(CleanupStepKind.Delete, $"Delete the emptied folder {mods.Path}", mods.Path);
        }
    }

    private static IEnumerable<CleanupStep> LinkSteps(Folder folder)
    {
        foreach (var link in folder.Entries.Where(e => e.Kind == InventoryKind.Link && !e.Path.Contains('/') && !folder.IsHandled(e)))
        {
            folder.Handle(link.Path);
            yield return new CleanupStep(
                CleanupStepKind.Delete,
                $"Delete link {link.Path} (points to {link.LinkTarget}; only the link is removed, what it points to is left alone)",
                link.Path);
        }
    }

    private static IEnumerable<CleanupStep> LeftoverSteps(Folder folder, CleanupRules rules)
    {
        foreach (var entry in folder.Entries.Where(e => e.Kind != InventoryKind.Link && !e.Path.Contains('/') && !folder.IsHandled(e)))
        {
            if (entry.Path.Equals(ProtonShaderCache, StringComparison.OrdinalIgnoreCase))
            {
                folder.Handle(entry.Path);
                continue;
            }

            if (!rules.Leftovers.Contains(entry.Path, StringComparer.OrdinalIgnoreCase) || folder.InBaseline(entry.Path)) continue;

            folder.Handle(entry.Path);
            yield return new CleanupStep(CleanupStepKind.Delete, entry.Kind == InventoryKind.Directory ? $"Delete {entry.Path}/ ({Count(folder.Under(entry.Path).Count(e => e.Kind != InventoryKind.Directory), "file")})" : $"Delete {entry.Path}", entry.Path);
        }

        foreach (var dump in folder.Entries.Where(e => e.Kind == InventoryKind.File && e.Path.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase) && !folder.IsHandled(e) && !folder.InBaseline(e.Path)))
        {
            folder.Handle(dump.Path);
            yield return new CleanupStep(CleanupStepKind.Delete, $"Delete {dump.Path}", dump.Path);
        }
    }

    private static IEnumerable<CleanupStep> ReviewSteps(Folder folder, FromSoftGameLayout layout)
    {
        if (!folder.HasBaseline) yield break;

        foreach (var entry in folder.Entries.Where(e => e.Kind != InventoryKind.Directory && !folder.IsHandled(e) && !folder.InBaseline(e.Path) && !IsGameArchive(layout, e.Path)))
            yield return new CleanupStep(CleanupStepKind.Review, $"Review {entry.Path}: it does not match the game's own file list, so it is left as it is", entry.Path);
    }

    // Indexing never hashes the game's archives and videos, so the app's file list can leave them out and the
    // folder still holds them as shipped.
    private static bool IsGameArchive(FromSoftGameLayout layout, string path)
        => FromSoftPaths.IsArchiveOrMedia(layout, new GamePath(LocationId.Game, $"{layout.GameSubfolder}/{path}"));

    private static string[] Segments(string path) => path.Split('/');

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    /// <summary>The inventory with the paths earlier steps have taken care of, so no entry gets two steps.</summary>
    private sealed class Folder(CleanupInput input)
    {
        private readonly List<string> _handled = [];

        public IReadOnlyList<InventoryEntry> Entries => input.Inventory;
        public bool HasBaseline => input.Baseline is not null;

        public InventoryEntry? Find(string path) => Entries.FirstOrDefault(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

        public IEnumerable<InventoryEntry> Under(string path) => Entries.Where(e => e.Path.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase));

        public bool InBaseline(string path) => input.Baseline?.Contains(path) == true;

        public void Handle(string path) => _handled.Add(path);

        public bool IsHandled(InventoryEntry entry)
            => _handled.Any(h => entry.Path.Equals(h, StringComparison.OrdinalIgnoreCase) || entry.Path.StartsWith(h + "/", StringComparison.OrdinalIgnoreCase));
    }
}
