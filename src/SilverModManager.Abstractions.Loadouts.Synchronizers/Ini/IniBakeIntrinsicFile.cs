using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;

/// <summary>
/// A game INI the loadout owns: its baseline with the enabled tweaks merged in and the keys the app manages set on
/// top. Rendering only reads the database and the file store; <see cref="GameInis.Prepare"/> is what stores the
/// baseline and records what was baked.
/// </summary>
public sealed class IniBakeIntrinsicFile(GamePath path, IGameIniFiles game, IFileStore fileStore) : IIntrinsicFileWithReset
{
    public GamePath Path { get; } = path;

    public async Task Write(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree)
    {
        var bytes = await GameInis.Render(game, fileStore, loadout, Path);
        await stream.WriteAsync(bytes);
    }

    /// <summary>
    /// The player's own copy, as it was before the loadout took the file over. The tweaks and managed keys belong to
    /// the loadout, so a reset leaves them out.
    /// </summary>
    public async Task<byte[]?> RenderForReset(Loadout.ReadOnly lastSyncedLoadout, IReadOnlyDictionary<GamePath, SyncNode> desiredState) =>
        await GameInis.LoadBaseline(fileStore, lastSyncedLoadout, Path);

    /// <summary>
    /// Leaves the loadout alone: a change the game made to the file is replaced by the next write, and the baseline
    /// stays as the player had it.
    /// </summary>
    public Task Ingest(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree, SilverModManager.MnemonicDB.Abstractions.ITransaction tx) =>
        Task.CompletedTask;
}
