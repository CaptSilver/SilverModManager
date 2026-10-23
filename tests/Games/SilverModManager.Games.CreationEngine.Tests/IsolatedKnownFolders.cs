using GameFinder.Wine;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.CreationEngine.Tests;

/// <summary>
/// A file system whose Documents and AppData folders live in a throwaway prefix. Passed as the
/// <c>locationsFileSystem</c> of <c>AddUniversalGameLocator</c>, it keeps Plugins.txt and the ini files out of
/// the real home and XDG folders, which every test in the run would otherwise share.
/// </summary>
internal static class IsolatedKnownFolders
{
    public static IFileSystem Create(IServiceProvider provider)
    {
        var pfx = provider.GetRequiredService<TemporaryFileManager>().CreateFolder("pfx");
        var prefix = new WinePrefix { ConfigurationDirectory = pfx.Path };
        var overlay = prefix.CreateOverlayFileSystem(FileSystem.Shared);
        overlay.GetKnownPath(KnownPath.MyDocumentsDirectory).CreateDirectory();
        overlay.GetKnownPath(KnownPath.LocalApplicationDataDirectory).CreateDirectory();
        return overlay;
    }
}
