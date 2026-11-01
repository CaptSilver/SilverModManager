using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Generic;

namespace SilverModManager.Games.Unreal;

/// <summary>
/// An Unreal Engine game whose mods are paks, Blueprint mods and UE4SS mods, described by an
/// <see cref="UnrealGameDefinition"/>. Every such game is an instance of this class, so nothing may look one up by type.
/// </summary>
public sealed class UnrealGame(IServiceProvider provider, UnrealGameDefinition definition)
    : DefinedGame<UnrealGameDefinition>(provider, definition)
{
    protected override ILoadoutSynchronizer CreateSynchronizer(IServiceProvider provider) => new UnrealGameSynchronizer(provider);

    protected override ILibraryItemInstaller[] CreateInstallers(IServiceProvider provider) =>
        [new UnrealModInstaller(provider, Definition.Layout)];
}
