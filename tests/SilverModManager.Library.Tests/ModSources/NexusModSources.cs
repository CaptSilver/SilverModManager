using NSubstitute;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Library.Tests.ModSources;

/// <summary>
/// Builds a <c>NexusModSource</c> whose collaborators are substitutes unless a test supplies its own.
/// </summary>
internal static class NexusModSources
{
    public static NexusModSource Create(
        IConnection connection,
        IModUpdateService? updates = null,
        ILoginManager? login = null,
        INexusUpdateDownloader? downloader = null,
        ILibraryService? library = null,
        IOSInterop? os = null,
        int maxParallelDownloads = 4)
    {
        var settings = Substitute.For<ISettingsManager>();
        settings.Get<DownloadSettings>().Returns(new DownloadSettings { MaxParallelDownloads = maxParallelDownloads });
        return new NexusModSource(
            updates ?? Substitute.For<IModUpdateService>(),
            login ?? Substitute.For<ILoginManager>(),
            downloader ?? Substitute.For<INexusUpdateDownloader>(),
            library ?? Substitute.For<ILibraryService>(),
            connection,
            settings,
            os ?? Substitute.For<IOSInterop>());
    }
}
