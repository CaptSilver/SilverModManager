using JetBrains.Annotations;
using NexusMods.Paths;

namespace NexusMods.Sdk.Games;

/// <summary>
/// Works out which game a folder the user pointed at is an install of and records it, so the locators report it.
/// </summary>
[PublicAPI]
public interface IAddGameByPath
{
    /// <summary>
    /// Records <paramref name="folder"/> as an install of a known game, or says why it did not.
    /// </summary>
    /// <param name="folder">The install folder.</param>
    /// <param name="game">The game to record it as, when the folder alone does not say.</param>
    /// <param name="prefix">The Wine prefix a Windows build runs in.</param>
    /// <param name="wine">The wine binary that goes with <paramref name="prefix"/>, when it is not the default one.</param>
    AddGameResult Add(AbsolutePath folder, GameId? game, AbsolutePath? prefix, AbsolutePath? wine);
}

/// <summary>
/// What <see cref="IAddGameByPath.Add"/> did. Only <see cref="Added"/> writes anything.
/// </summary>
[PublicAPI]
public abstract record AddGameResult
{
    private AddGameResult() { }

    /// <summary>
    /// The install is recorded and the game is listed. A manually added install carries no store build id, so
    /// managing it ('Add' on My Games) is refused by the baseline preflight, with the reason shown, until a
    /// baseline source for manual installs exists.
    /// </summary>
    public sealed record Added(IGameData Game) : AddGameResult;

    /// <summary>The folder is a Windows build and no Wine prefix was given, so it could not run. Nothing is written.</summary>
    public sealed record NeedsPrefix(IGameData Game) : AddGameResult;

    /// <summary>Several games launch <paramref name="File"/> and nothing in the folder tells them apart. Nothing is written.</summary>
    public sealed record Ambiguous(RelativePath File, IReadOnlyList<IGameData> Candidates) : AddGameResult;

    /// <summary>A store client or an earlier add already reports this folder. Nothing is written.</summary>
    public sealed record AlreadyDetected(IGameData Game, GameStore Store) : AddGameResult;

    /// <summary>No known game launches from the folder. Nothing is written.</summary>
    public sealed record Unknown : AddGameResult;

    /// <summary>The record could not be stored; <paramref name="Message"/> says why.</summary>
    public sealed record StoreUnavailable(string Message) : AddGameResult;
}
