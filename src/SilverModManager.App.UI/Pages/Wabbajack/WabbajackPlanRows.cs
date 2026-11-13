using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts.Install;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Install;

namespace SilverModManager.App.UI.Pages.Wabbajack;

/// <summary>
/// The mod rows of a list that is not installing yet: each folder waits, or shows that its main archive is being
/// downloaded or is waiting for the user, as the archive table says.
/// </summary>
internal static class WabbajackPlanRows
{
    /// <summary>One row per folder, all waiting. Built once per pick, in the list's folder order.</summary>
    internal static ModInstallRows Build(IReadOnlyList<ModFolderPlan> folders)
    {
        var rows = new ModInstallRows();
        rows.Add(folders.OrderBy(f => f.Index).Select(f => new ModInstallRow(f.Folder, f.Folder, f.Index, new ModInstallRowState(ModInstallState.Waiting, 0, 0, f.Total))));
        rows.SetTotals(new ModInstallTotals(rows.Count, 0, 0));
        return rows;
    }

    /// <summary>
    /// Sets each row from the state of its folder's main archive in <paramref name="plan"/>. A row is only set when its
    /// state differs, so a plan change that moves nothing makes no new objects and raises nothing.
    /// </summary>
    internal static void Update(ModInstallRows rows, IReadOnlyDictionary<string, FileIdentity> mainArchiveByFolder, ArchivePlan plan)
    {
        var archives = new Dictionary<FileIdentity, ArchiveState>();
        foreach (var archive in plan.Rows) archives.TryAdd(archive.Archive.XxHash64, archive.State);

        foreach (var row in rows.Snapshot())
        {
            var state = mainArchiveByFolder.TryGetValue(row.Key, out var main) && archives.TryGetValue(main, out var archiveState)
                ? StateOf(archiveState)
                : ModInstallState.Waiting;
            if (row.Current.State != state) row.Set(row.Current with { State = state });
        }
    }

    // Nothing is fetched for a held archive, a game file or Mod Organizer itself, so the folder just waits for its turn.
    // An archive the app cannot fetch, or a game file that differs, is the user's to sort out like a manual download.
    private static ModInstallState StateOf(ArchiveState archive) => archive switch
    {
        ArchiveState.Have or ArchiveState.GameFile or ArchiveState.NotNeeded => ModInstallState.Waiting,
        ArchiveState.Unsupported or ArchiveState.GameFileVersion => ModInstallState.NeedsUser,
        _ when archive.WaitsForUser() => ModInstallState.NeedsUser,
        _ => ModInstallState.Downloading,
    };
}
