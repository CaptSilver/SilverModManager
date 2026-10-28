using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Diagnostics;

/// <summary>
/// Warns about a loadout file the app holds no copy of and that is no longer in the game folder. Files kept where
/// they were (over the backup cap) end up like this when something outside the app deletes them, such as a mod
/// manager purging its hardlinks; the synchronizer only logs that on every sync.
/// </summary>
public class MissingUnbackedFileEmitter(IFileStore fileStore) : ILoadoutDiagnosticEmitter
{
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        // The disk state describes whichever loadout was applied last, so any other loadout would be compared
        // against files that were never meant to be its own.
        if (!GameInstallMetadata.LastSyncedLoadout.TryGetValue(loadout.Installation, out var applied) || applied != loadout.Id) yield break;

        var onDisk = new HashSet<GamePath>();
        foreach (var entry in DiskStateEntry.FindByGame(loadout.Db, loadout.Installation))
            onDisk.Add(entry.Path);

        foreach (var file in LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemWithTargetPath().OfTypeLoadoutFile())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A disabled file is not meant to be in the folder.
            if (!file.AsLoadoutItemWithTargetPath().AsLoadoutItem().IsEnabled()) continue;
            GamePath path = file.AsLoadoutItemWithTargetPath().TargetPath;
            // A file that is there but different is changed, not gone.
            if (onDisk.Contains(path)) continue;
            if (await fileStore.HaveFile(file.Hash)) continue;

            // Titles take no message data, so the path goes into the title by hand to tell the entries apart.
            var name = path.Path.ToString();
            yield return MissingUnbackedFileDiagnostics.CreateMissingUnbackedFile(name) with { Title = $"{name} is gone and has no backup copy" };
        }
    }
}
