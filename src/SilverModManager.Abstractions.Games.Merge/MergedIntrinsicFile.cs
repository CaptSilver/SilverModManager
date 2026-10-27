using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games.Merge;

/// <summary>
/// One merged file at <paramref name="path"/>. It copies the bytes the merge plan holds for <paramref name="relPath"/>
/// and never merges on its own: the preflight has already planned (and refused, if it had to), and a second computation
/// here could disagree with the one that was checked.
/// </summary>
public class MergedIntrinsicFile(IMergePlanSource plans, GamePath path, string relPath) : IIntrinsicFile
{
    public GamePath Path { get; } = path;

    /// <summary>The path below the mod folder that the plan keys this file by.</summary>
    public string RelPath { get; } = relPath;

    public async Task Write(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree)
    {
        var bytes = plans.MergedBytes(loadout, RelPath)
                    ?? throw new InvalidOperationException($"{Path.Path} has no merged bytes in the merge plan: the plan and the sync tree disagree about which files are merged");
        await stream.WriteAsync(bytes);
    }

    /// <summary>
    /// The synchronizer hands over a file that is not what it last wrote. The loadout does not change: the file is
    /// derived from the mods, and an edit to it belongs in one of them. The same apply writes the generated file
    /// over the edit.
    /// </summary>
    public Task Ingest(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree, ITransaction tx)
        => Task.CompletedTask;
}
