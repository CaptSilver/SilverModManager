using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.FromSoft.Me3;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.FromSoft;

/// <summary>
/// A FromSoft game that me3 launches and has nothing of its own beyond its definition: the loader, the synchronizer and
/// the installers are the family's, so the game class holds only the three statics and the definition.
/// </summary>
public abstract class AMe3FromSoftGame<TSelf> : AFromSoftGame<TSelf>
    where TSelf : AMe3FromSoftGame<TSelf>, IGameData<TSelf>
{
    protected AMe3FromSoftGame(IServiceProvider provider, FromSoftGameDefinition definition, Me3LoaderOptions options)
        : base(provider, definition, _ => new Me3Loader(provider, options, definition)) { }

    protected override ILoadoutSynchronizer CreateSynchronizer(IServiceProvider provider) => new FromSoftSynchronizer(provider, this);

    /// <remarks>The host installer goes first because a release of the loader can hold a lone DLL that the native installer would take for a mod.</remarks>
    protected sealed override ILibraryItemInstaller[] CreateInstallers(IServiceProvider provider) =>
    [
        Loader.HostInstaller(),
        ..GameInstallers(provider),
        ..Loader.ModInstallers(),
    ];

    /// <summary>Installers for the game's own mods, tried after the loader's host installer and before its generic ones.</summary>
    protected virtual IEnumerable<ILibraryItemInstaller> GameInstallers(IServiceProvider provider) => [];
}
