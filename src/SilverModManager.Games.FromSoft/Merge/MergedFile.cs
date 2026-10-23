using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.FromSoft.Me3;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Merge;

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
    /// The synchronizer hands over a file that is not what it last wrote. The loadout does not change: the file is
    /// derived from the mods, and an edit to it belongs in one of them. The same apply writes the generated file
    /// over the edit.
    /// </summary>
    public Task Ingest(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree, ITransaction tx)
        => Task.CompletedTask;

    private byte[] Planned(Loadout.ReadOnly loadout)
    {
        var plan = plans.Current(loadout, MergeSourceMode.Apply);
        var merged = plan.Merged.FirstOrDefault(m => m.Path.Equals(relPath, StringComparison.OrdinalIgnoreCase));
        return merged?.Bytes
               ?? throw new InvalidOperationException($"{Path.Path} has no merged bytes in the merge plan: the plan and the sync tree disagree about which files are merged");
    }
}
