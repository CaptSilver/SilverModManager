using GameFinder.Wine;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;

namespace SilverModManager.Games.TestFramework;

/// <summary>
/// A file system whose Documents and AppData folders live in a throwaway prefix of the calling fixture. Passed as
/// the <c>locationsFileSystem</c> of <c>AddUniversalGameLocator</c> (or the stubbed locator), it keeps game
/// locations that sit under the user's known folders out of the real home and XDG folders. Without it every
/// fixture in a run resolves them to the same XDG data folder and parallel classes trample each other's files.
/// </summary>
public static class IsolatedKnownFolders
{
    public static IFileSystem Create(IServiceProvider provider)
    {
        var pfx = provider.GetRequiredService<TemporaryFileManager>().CreateFolder("pfx");
        var prefix = new WinePrefix { ConfigurationDirectory = pfx.Path };
        var overlay = prefix.CreateOverlayFileSystem(FileSystem.Shared);
        overlay.GetKnownPath(KnownPath.LocalApplicationDataDirectory).CreateDirectory();
        overlay.GetKnownPath(KnownPath.MyDocumentsDirectory).CreateDirectory();
        return overlay;
    }
}
