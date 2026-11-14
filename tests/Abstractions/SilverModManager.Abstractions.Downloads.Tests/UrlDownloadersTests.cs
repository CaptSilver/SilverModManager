using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.Abstractions.Downloads.Tests;

public class UrlDownloadersTests
{
    private sealed class FakeHost(string name, string host) : IUrlDownloader
    {
        public string Name => name;
        public bool CanFetch(Uri url) => url.Host == host;
        public IJobTask<IDownloadJob, AbsolutePath> Create(IServiceProvider provider, Uri url, string fileName, AbsolutePath destination)
            => throw new NotSupportedException();
    }

    [Fact]
    public void TheFirstDownloaderThatCanFetchWins()
    {
        var first = new FakeHost("first", "files.example");
        var second = new FakeHost("second", "files.example");
        var downloaders = new UrlDownloaders([new FakeHost("other", "other.example"), first, second]);

        downloaders.For(new Uri("https://files.example/a.zip")).Should().BeSameAs(first);
    }

    [Fact]
    public void NoDownloaderMeansNull()
    {
        var downloaders = new UrlDownloaders([new FakeHost("only", "files.example")]);

        downloaders.For(new Uri("https://nowhere.example/a.zip")).Should().BeNull();
    }

    [Fact]
    public void TheDownloadersAreListedInRegistrationOrder()
    {
        var a = new FakeHost("a", "a.example");
        var b = new FakeHost("b", "b.example");

        new UrlDownloaders([a, b]).All.Should().Equal(a, b);
    }
}
