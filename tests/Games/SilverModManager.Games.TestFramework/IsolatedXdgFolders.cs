using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;

namespace SilverModManager.Games.TestFramework;

/// <summary>
/// A file system whose XDG config and data folders live in a throwaway folder of the calling fixture. Passed as the
/// <c>locationsFileSystem</c> of <c>AddUniversalGameLocator</c> for a native Linux build, it keeps a location under
/// the XDG config folder out of the real home.
/// </summary>
public static class IsolatedXdgFolders
{
    public static IFileSystem Create(IServiceProvider provider)
    {
        var root = provider.GetRequiredService<TemporaryFileManager>().CreateFolder("xdg").Path;
        var config = root.Combine("config");
        var data = root.Combine("data");
        config.CreateDirectory();
        data.CreateDirectory();

        return FileSystem.Shared.CreateOverlayFileSystem(
            new Dictionary<AbsolutePath, AbsolutePath>(),
            new Dictionary<KnownPath, AbsolutePath> { [KnownPath.XDG_CONFIG_HOME] = config, [KnownPath.XDG_DATA_HOME] = data });
    }
}
