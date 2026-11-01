using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Games.Generic.Loaders;

namespace SilverModManager.Games.REEngine;

/// <summary>
/// The REFramework loader of an RE Engine game: the dinput8.dll proxy and the <c>reframework</c> folder its scripts and
/// plugins are read from come from <see cref="AProxyDllLoader"/>; the release name comes from the game's definition.
/// Loose <c>natives</c> files do not count as mods here: the game reads those itself.
/// </summary>
/// <remarks>
/// An RE Engine game implements <see cref="SilverModManager.Abstractions.Games.IWineDllOverrideRequirements"/> by
/// forwarding to its loader (REFramework's README asks for <c>WINEDLLOVERRIDES="dinput8.dll=n,b"</c>).
/// </remarks>
public sealed class ReFrameworkLoader(ReEngineGameDefinition definition) : AProxyDllLoader(ReFrameworkFiles.Loader, ReFrameworkFiles.Root)
{
    /// <summary>The file in REFramework's releases to download for this game.</summary>
    public string Release => definition.ReFrameworkRelease;

    public ILibraryItemInstaller[] Installers(IServiceProvider provider) => [new ReFrameworkPackInstaller(provider), new ReEngineModInstaller(provider)];
}
