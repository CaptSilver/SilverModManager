using System.Runtime.InteropServices;
using JetBrains.Annotations;

namespace NexusMods.Sdk.Games;

/// <summary>
/// A game install the user pointed the app at, instead of one a store client reported.
/// </summary>
/// <param name="Game">The game this folder is an install of.</param>
/// <param name="Path">The install folder.</param>
/// <param name="Platform">Which build is in the folder. A Windows build needs a <paramref name="Prefix"/> to run.</param>
/// <param name="Prefix">The Wine prefix the install runs in, when it has one.</param>
/// <param name="Wine">The wine binary that goes with <paramref name="Prefix"/>, when it is not the default one.</param>
[PublicAPI]
public sealed record UserGameInstall(GameId Game, string Path, OSPlatform Platform, string? Prefix = null, string? Wine = null);

/// <summary>
/// The installs the user recorded by hand. Locators read it; add-by-path writes it.
/// </summary>
[PublicAPI]
public interface IUserGameInstallStore
{
    /// <summary>
    /// Every valid record, in the order they were added. Re-read on each call so edits made while the app runs show up.
    /// </summary>
    IReadOnlyList<UserGameInstall> GetAll();

    /// <summary>
    /// Appends a record. Callers that need locators to see it clear <see cref="IGameRegistry"/>'s cache afterwards.
    /// </summary>
    void Add(UserGameInstall install);
}
