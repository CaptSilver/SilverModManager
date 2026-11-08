using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Mapping;
using SilverModManager.Wabbajack.Models;
using Placement = SilverModManager.Wabbajack.Install.InstallModlistJob.Placement;

namespace SilverModManager.Wabbajack.Update;

/// <summary>
/// What moving an installed list to a newer version of it would do, worked out without doing any of it. The folders and
/// files are the install's own decisions (<see cref="InstallReconciler"/>) run against the rows the group holds.
/// <paramref name="Downloads"/> are the archives the library lacks; <paramref name="NewBytesUpperBound"/> is the sum of
/// the sizes of the files still to be written, an upper bound because the store holds identical bytes once.
/// The folder lists name the list's mod folders.
/// </summary>
public sealed record ModlistUpdatePreviewResult(
    IReadOnlyList<ArchivePlanRow> Downloads, Size DownloadSize, Size NewBytesUpperBound,
    IReadOnlyList<string> Added, IReadOnlyList<string> Changed, IReadOnlyList<(string From, string To)> Renamed, IReadOnlyList<string> Removed,
    IReadOnlyList<string> RetriedTextures, IReadOnlyList<RelativePath> GameFilesKept, IReadOnlyList<GamePath> UserTweaksKept,
    IReadOnlyList<string> Warnings);

/// <summary>
/// A read-only review of an update. It is not an install job: it opens no transaction, pins nothing and registers no
/// run. It runs the install's check phase and reconciliation functions against the new list and the group as it stands.
/// </summary>
public sealed class ModlistUpdatePreview(IServiceProvider provider)
{
    /// <summary>Shown when the library no longer holds the installed version, so there is nothing to tell which files the user changed or kept.</summary>
    public const string InstalledVersionGoneWarning = "the installed version's files are gone from the library; the list's order and flags are applied in full";

    /// <summary>
    /// Parses the new list, builds its archive plan, reads the group's rows and runs the install's reconciliation on them.
    /// Changes nothing.
    /// </summary>
    public async Task<ModlistUpdatePreviewResult> Preview(WabbajackModlistLoadoutGroup.ReadOnly group, WabbajackModlistLibraryFile.ReadOnly newFile, CancellationToken ct)
    {
        var connection = provider.GetRequiredService<IConnection>();
        var fileStore = provider.GetRequiredService<IFileStore>();
        var logger = provider.GetRequiredService<ILogger<ModlistUpdatePreview>>();
        var db = connection.Db;

        var loadoutId = group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().LoadoutId;
        var groupId = group.AsCollectionGroup().AsLoadoutItemGroup().LoadoutItemGroupId;

        var check = await InstallModlistJob.CheckOnly(
            loadoutId, newFile, connection, fileStore, provider.GetRequiredService<ArchiveInventory>(), logger, new ModlistInstallProgress(), ct);
        var state = InstallModlistJob.LoadExisting(db, groupId);

        var skips = WabbajackSkippedFile.FindByGroup(db, group.Id).ToArray();
        var keptOriginals = skips.Where(s => s.Kind == SkipKind.KeptOriginal).Select(s => s.To).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The same finished-directive decision the install makes when it continues an update.
        var executable = InstallModlistJob.Executable(check.Placements);
        var finished = InstallReconciler.FinishedDirectives(
            state, executable, keptOriginals, updateMode: true,
            path => InstallModlistJob.OwnedIniPath(check, path) is not null,
            (directive, stored) => InstallModlistJob.IsLibraryCopyOf(db, directive, stored),
            (_, _) => { });
        var done = finished.Select(p => p.Index).ToHashSet();
        var unfinished = executable.Where(p => !done.Contains(p.Index)).ToArray();

        var gone = InstallReconciler.GoneFolders(state, check.Placements);
        var renamed = InstallReconciler.RenamedFolders(state, check.Placements, gone);
        var renamedTo = renamed.Select(r => r.To).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var renamedFrom = renamed.Select(r => r.From).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var named = InstallReconciler.NamedFolders(check.Placements);
        var added = named.Where(folder => !state.Files.ContainsKey(folder) && !renamedTo.Contains(folder)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var changed = ChangedFolders(check, state, unfinished);
        var removed = gone.Where(folder => !renamedFrom.Contains(folder)).Order(StringComparer.OrdinalIgnoreCase).ToArray();

        var retried = unfinished
            .Where(p => keptOriginals.Contains(p.To.ToString()))
            .Select(p => p.To.ToString())
            .ToArray();

        var isListOutput = await InstallModlistJob.ListOutputRows(fileStore, state, check.List.Name);
        var (_, userTweaks) = InstallReconciler.StaleRows(state, isListOutput);

        var warnings = new List<string>();
        var previous = await InstallModlistJob.ClassifyVersion(WabbajackModlistLibraryFile.Load(db, group.ModlistId.Value), loadoutId, connection, fileStore, ct);
        if (previous is null) warnings.Add(InstalledVersionGoneWarning);

        var downloads = check.Archives.Missing;
        var downloadSize = Size.From((ulong)downloads.Sum(row => row.Archive.Size));
        var newBytes = Size.From((ulong)unfinished.Sum(p => p.Directive.Size));
        return new ModlistUpdatePreviewResult(
            downloads, downloadSize, newBytes, added, changed, renamed, removed, retried,
            GameFilesKept(check, state, group, skips, previous), userTweaks, warnings);
    }

    /// <summary>The folders that already exist and have a file still to write; a directive no earlier run could have finished does not count.</summary>
    private static string[] ChangedFolders(InstallModlistJob.Checked check, InstallModlistJob.WriteState state, IReadOnlyList<Placement> unfinished)
    {
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var placement in unfinished)
        {
            if (placement.Group is not { } folder || placement.Path is not { } path) continue;
            if (folder.Equals(Mo2Layout.ProfileSettingsGroup, StringComparison.OrdinalIgnoreCase) || !state.Files.ContainsKey(folder)) continue;
            if (InstallReconciler.CanBeFinished(placement, path, installPath => InstallModlistJob.OwnedIniPath(check, installPath) is not null))
                changed.Add(folder);
        }
        return changed.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>The game files the user kept, as the install works them out; none when there is no previous version to compare with.</summary>
    private static RelativePath[] GameFilesKept(
        InstallModlistJob.Checked check, InstallModlistJob.WriteState state, WabbajackModlistLoadoutGroup.ReadOnly group,
        IReadOnlyList<WabbajackSkippedFile.ReadOnly> skips, InstallModlistJob.Classified? previous)
    {
        if (previous is null || group.InstalledAt is not { HasValue: true }) return [];

        var skipped = skips.Select(s => s.To).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return InstallModlistJob.GameFilesKept(state, check.Placements, skipped, previous.Placements, previous.List).Order().ToArray();
    }
}
