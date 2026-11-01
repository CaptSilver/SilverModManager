using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Games.Generic.Installers;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Unreal;

/// <summary>Installs paks, Blueprint mods and UE4SS mods where the game's <see cref="UnrealModLayout"/> puts them.</summary>
public sealed class UnrealModInstaller(IServiceProvider serviceProvider, UnrealModLayout layout)
    : ARoutedArchiveInstaller(serviceProvider, serviceProvider.GetRequiredService<ILogger<UnrealModInstaller>>())
{
    protected override string ExpectedFiles => "paks, LogicMods or UE4SS mods";

    protected override bool TryRoute(IReadOnlyList<RelativePath> archive, RelativePath path, out GamePath target) => layout.TryRoute(path, out target);
}
