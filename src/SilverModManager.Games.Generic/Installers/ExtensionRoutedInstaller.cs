using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Games.Generic.Simple;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Generic.Installers;

/// <summary>Sends each file to the folder its extension belongs in, as the <see cref="SimpleExtensionRoute"/>s of a game's rules say.</summary>
public sealed class ExtensionRoutedInstaller(IServiceProvider serviceProvider, ModLocation modRoot, IReadOnlyList<SimpleExtensionRoute> routes, bool leaveOutUnrouted)
    : ARoutedArchiveInstaller(serviceProvider, serviceProvider.GetRequiredService<ILogger<ExtensionRoutedInstaller>>())
{
    protected override string ExpectedFiles => string.Join(", ", routes.SelectMany(route => route.Extensions).Select(extension => extension.ToString()));

    protected override bool IsLeftOut(RelativePath path) => leaveOutUnrouted || base.IsLeftOut(path);

    protected override bool TryRoute(IReadOnlyList<RelativePath> archive, RelativePath path, out GamePath target)
    {
        var route = routes.FirstOrDefault(route => route.Extensions.Contains(path.Extension));
        if (route is null)
        {
            target = default;
            return false;
        }

        var folders = path.Parts.SkipLast(1).Select(part => part.ToString()).ToArray();
        var kept = folders.Skip(Math.Max(0, folders.Length - route.KeepFolders));
        var inside = kept.Aggregate(route.Destination, (folder, name) => folder.Join(name)).Join(path.FileName);
        target = modRoot.ToGamePath(inside);
        return true;
    }
}
