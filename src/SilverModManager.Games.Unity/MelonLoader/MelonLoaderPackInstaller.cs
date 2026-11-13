using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;

namespace SilverModManager.Games.Unity.MelonLoader;

/// <summary>
/// Installs the MelonLoader loader pack: <c>version.dll</c>, the <c>MelonLoader</c> folder and their siblings land at the
/// game root, with at most one wrapping folder left off.
/// </summary>
public sealed class MelonLoaderPackInstaller(IServiceProvider serviceProvider, MelonLoaderLoader loader)
    : AUnityLoaderPackInstaller(serviceProvider, serviceProvider.GetRequiredService<ILogger<MelonLoaderPackInstaller>>(), loader)
{
    protected override bool IsLoaderPack(RelativePath[] paths, out string root) => MelonLoaderLayout.IsLoaderPack(paths, out root);

    protected override string PackLayoutDescription => $"folder with {MelonLoaderFiles.Proxy} and a {MelonLoaderFiles.Root}/ folder";
}
