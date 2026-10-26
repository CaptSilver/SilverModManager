using DynamicData.Kernel;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Games;

/// <summary>
/// Interface for a specific game recognized by the app. A single game can have
/// multiple installations.
/// </summary>
public interface IGame : IGameData, IHasSynchronizer
{
    /// <summary>
    /// Gets all available installers this game supports.
    /// </summary>
    ILibraryItemInstaller[] LibraryItemInstallers { get; }

    /// <summary>
    /// An array of all instances of <see cref="IDiagnosticEmitter"/> supported
    /// by the game.
    /// </summary>
    IDiagnosticEmitter[] DiagnosticEmitters { get; }

    /// <summary>
    /// The sort order manager for this game.
    /// </summary>
    ISortOrderManager SortOrderManager { get; }
}
