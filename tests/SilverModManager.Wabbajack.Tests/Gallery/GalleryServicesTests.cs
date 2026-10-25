using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Wabbajack.Gallery;

namespace SilverModManager.Wabbajack.Tests.Gallery;

public class GalleryServicesTests(IServiceProvider provider)
{
    [Fact]
    public void TheGalleryClientAndDownloaderAreRegistered()
    {
        provider.GetRequiredService<WabbajackGalleryClient>().Should().NotBeNull();
        provider.GetRequiredService<WabbajackGalleryDownloader>().Should().NotBeNull();
    }

    [Fact]
    public void TheGalleryCacheIsUnderTheTestRoot()
    {
        provider.GetRequiredService<WabbajackPaths>().GalleryCache.ToString().Should().Contain(Startup.SettingsRoot);
    }

    [Theory]
    [InlineData("https://authored-files.wabbajack.org/x.wabbajack_1", true)]
    [InlineData("https://wabbajack.b-cdn.net/x.wabbajack_1", true)]
    [InlineData("https://example.invalid/x.wabbajack", false)]
    public void OnlyWabbajackCdnAddressesUseTheCdnDownload(string url, bool cdn)
    {
        ModlistDownloadJobs.IsWabbajackCdn(new Uri(url)).Should().Be(cdn);
    }
}
