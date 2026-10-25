using JetBrains.Annotations;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Diagnostics.Emitters;

/// <summary>
/// What an update check sees of one game: the loadout, the installation the registry cached at startup
/// (good for paths), and a fresh locate of the same installation (the source for anything the store
/// writes while the app runs, such as the build id and the update time).
/// </summary>
/// <param name="Loadout">The loadout being diagnosed.</param>
/// <param name="Installation">The cached installation. Do not read store fields from it; they are as old as the app.</param>
/// <param name="Sensitivity">The game's own answer on how an update affects it.</param>
/// <param name="Fresh">The installation as the store reports it now, or the cached one when it could not be located again.</param>
[PublicAPI]
public sealed record GameUpdateContext(
    Loadout.ReadOnly Loadout,
    GameInstallation Installation,
    IGameUpdateSensitivity Sensitivity,
    GameLocatorResult Fresh);

/// <summary>
/// One note about a game having been updated. Run by the shared update emitter for games that implement
/// <see cref="IGameUpdateSensitivity"/>.
/// </summary>
[PublicAPI]
public interface IGameUpdateCheck
{
    /// <summary>
    /// Creates the diagnostics for <paramref name="context"/>, or none.
    /// </summary>
    IEnumerable<Diagnostic> Check(GameUpdateContext context);
}
