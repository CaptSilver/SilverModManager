using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.FromSoft.Me3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FromSoft.Merge;

/// <summary>
/// One merged file in the merged package of the mods folder. It copies the bytes the merge plan holds for its path
/// and never merges on its own: the preflight has already planned (and refused, if it had to), and a second computation
/// here could disagree with the one that was checked.
/// </summary>
public sealed class MergedFile(MergePlanCache plans, string relPath) : IIntrinsicFile
{
    public GamePath Path { get; } = new(LocationId.Me3Mods, $"{Me3ModsLayout.MergedPackageId}/{relPath}");

    public async Task Write(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree)
        => await stream.WriteAsync(Planned(loadout));

    /// <summary>
    /// The synchronizer hands over a file that is not what it last wrote. The generated one replaces it right away,
    /// because the write would otherwise only happen on the apply after the one that noticed the edit. The loadout
    /// does not change: the file is derived from the mods, and an edit to it belongs in one of them.
    /// </summary>
    public async Task Ingest(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree, ITransaction tx)
    {
        var generated = Planned(loadout);

        using var onDisk = new MemoryStream();
        await stream.CopyToAsync(onDisk);
        if (onDisk.GetBuffer().AsSpan(0, (int)onDisk.Length).SequenceEqual(generated)) return;

        // Renamed over the file because the synchronizer still holds the old one open for reading.
        var target = loadout.InstallationInstance.Locations.ToAbsolutePath(Path);
        var replacement = target.Parent / $"{target.FileName}.new";
        await File.WriteAllBytesAsync(replacement.GetFullPath(), generated);
        await replacement.MoveToAsync(target, overwrite: true);
    }

    private byte[] Planned(Loadout.ReadOnly loadout)
    {
        var plan = plans.Current(loadout, MergeSourceMode.Apply);
        var merged = plan.Merged.FirstOrDefault(m => m.Path.Equals(relPath, StringComparison.OrdinalIgnoreCase));
        return merged?.Bytes
               ?? throw new InvalidOperationException($"{Path.Path} has no merged bytes in the merge plan: the plan and the sync tree disagree about which files are merged");
    }
}
