using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Generic;

namespace SilverModManager.Games.Unreal;

/// <summary>
/// An Unreal Engine game whose mods are paks, Blueprint mods and UE4SS mods, described by an
/// <see cref="UnrealGameDefinition"/>. Every such game is an instance of this class, so nothing may look one up by type.
/// </summary>
/// <remarks>
/// <see cref="IDxgiGame"/> holds for every row: each one is a DX11 or DX12 title (DXVK or VKD3D-Proton on Linux). A row
/// that renders through Vulkan only would need its own flag on the definition instead of this blanket marker.
/// </remarks>
public sealed class UnrealGame(IServiceProvider provider, UnrealGameDefinition definition)
    : DefinedGame<UnrealGameDefinition>(provider, definition), IDxgiGame
{
    protected override ILoadoutSynchronizer CreateSynchronizer(IServiceProvider provider) => new UnrealGameSynchronizer(provider);

    protected override ILibraryItemInstaller[] CreateInstallers(IServiceProvider provider) =>
        [new UnrealModInstaller(provider, Definition.Layout)];
}
