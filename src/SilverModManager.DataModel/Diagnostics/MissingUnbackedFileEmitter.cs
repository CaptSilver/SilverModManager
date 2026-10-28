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

        var onDisk = new Dictionary<GamePath, NexusMods.Hashing.xxHash3.Hash>();
        foreach (var entry in DiskStateEntry.FindByGame(loadout.Db, loadout.Installation))
            onDisk[entry.Path] = entry.Hash;

        foreach (var file in LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemWithTargetPath().OfTypeLoadoutFile())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A disabled file is not meant to be in the folder.
            if (!file.AsLoadoutItemWithTargetPath().AsLoadoutItem().IsEnabled()) continue;
            GamePath path = file.AsLoadoutItemWithTargetPath().TargetPath;
            if (onDisk.TryGetValue(path, out var diskHash) && diskHash == file.Hash) continue;
            if (await fileStore.HaveFile(file.Hash)) continue;

            // Titles take no message data, so the path goes into the title by hand to tell the entries apart.
            var name = path.Path.ToString();
            yield return MissingUnbackedFileDiagnostics.CreateMissingUnbackedFile(name) with { Title = $"{name} is gone and has no backup copy" };
        }
    }
}
