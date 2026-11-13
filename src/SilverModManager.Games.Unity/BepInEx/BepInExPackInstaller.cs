using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;

namespace SilverModManager.Games.Unity.BepInEx;

/// <summary>
/// Installs a BepInEx loader pack. Its files sit under one root folder (<c>BepInExPack/</c>, <c>BepInExPack_PEAK/</c>, ...)
/// that is left off. The name match covers BepInEx 6 packs, which have no BepInEx.Preloader.dll.
/// </summary>
public sealed class BepInExPackInstaller(IServiceProvider serviceProvider, BepInExLoader loader)
    : AUnityLoaderPackInstaller(serviceProvider, serviceProvider.GetRequiredService<ILogger<BepInExPackInstaller>>(), loader)
{
    protected override bool IsLoaderPack(RelativePath[] paths, out string root) => BepInExLayout.IsLoaderPack(paths, out root);

    protected override string PackLayoutDescription =>
        $"folder with {BepInExFiles.DoorstopProxy}, {BepInExFiles.DoorstopConfig} and {BepInExFiles.Root}/{BepInExFiles.Core}/{BepInExFiles.Preloader}";
}
