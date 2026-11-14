namespace SilverModManager.Abstractions.Downloads;

/// <summary>
/// The registered <see cref="IUrlDownloader"/>s. A source that names a host's file registers its own downloader and
/// every caller that has an address finds it here.
/// </summary>
public sealed class UrlDownloaders(IEnumerable<IUrlDownloader> downloaders)
{
    private readonly IUrlDownloader[] _downloaders = downloaders.ToArray();

    /// <summary>Every downloader, in registration order.</summary>
    public IReadOnlyList<IUrlDownloader> All => _downloaders;

    /// <summary>The first downloader that can fetch <paramref name="url"/>, or null when none can and the user has to.</summary>
    public IUrlDownloader? For(Uri url) => _downloaders.FirstOrDefault(downloader => downloader.CanFetch(url));
}
