using System.Net;
using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.PrefixSetup;

namespace SilverModManager.Platform.Proton.Tests.PrefixSetup;

public class HttpPrefixFileFetcherTests
{
    private readonly InMemoryFileSystem _fs = new(OSInformation.FakeUnix);

    private AbsolutePath Destination => _fs.GetKnownPath(KnownPath.TempDirectory).Combine("cache/file.part");

    [Fact]
    public async Task Fetch_SavesTheResponseBody()
    {
        var fetcher = new HttpPrefixFileFetcher(new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }))));

        await fetcher.Fetch("https://example.invalid/file", Destination, CancellationToken.None);

        (await Destination.ReadAllBytesAsync()).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Fetch_AnHttpErrorIsAnIOException()
    {
        var fetcher = new HttpPrefixFileFetcher(new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)))));

        var act = () => fetcher.Fetch("https://example.invalid/file", Destination, CancellationToken.None);

        await act.Should().ThrowAsync<IOException>().WithMessage("*https://example.invalid/file*");
    }

    [Fact]
    public async Task Fetch_ATimeoutIsAnIOExceptionNotACancellation()
    {
        // HttpClient reports its own timeout as a TaskCanceledException; passed on, the runner would read it
        // as the user cancelling the setup instead of a failed download.
        var fetcher = new HttpPrefixFileFetcher(new HttpClient(new Handler((_, _) => throw new TaskCanceledException("timed out", new TimeoutException()))));

        var act = () => fetcher.Fetch("https://example.invalid/file", Destination, CancellationToken.None);

        await act.Should().ThrowAsync<IOException>().WithMessage("*timed out*");
    }

    [Fact]
    public async Task Fetch_TheCallersCancellationStaysACancellation()
    {
        using var cts = new CancellationTokenSource();
        var fetcher = new HttpPrefixFileFetcher(new HttpClient(new Handler((_, token) =>
        {
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("unreachable");
        })));

        var act = () => fetcher.Fetch("https://example.invalid/file", Destination, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
