using GameFinder.Wine;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;

namespace NexusMods.Games.Larian.Tests.BaldursGate3;

/// <summary>
/// A file system whose AppData folder lives in a throwaway prefix. Passed as the <c>locationsFileSystem</c> of
/// <c>AddUniversalGameLocator</c>, it keeps the Mods and PlayerProfiles locations out of the real home folder.
/// </summary>
internal static class IsolatedKnownFolders
{
    public static IFileSystem Create(IServiceProvider provider)
    {
        var pfx = provider.GetRequiredService<TemporaryFileManager>().CreateFolder("pfx");
        var prefix = new WinePrefix { ConfigurationDirectory = pfx.Path };
        var overlay = prefix.CreateOverlayFileSystem(FileSystem.Shared);
        overlay.GetKnownPath(KnownPath.LocalApplicationDataDirectory).CreateDirectory();
        return overlay;
    }
}
