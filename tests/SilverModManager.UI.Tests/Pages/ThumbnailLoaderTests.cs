using SilverModManager.App.UI.Controls.Cards;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using FluentAssertions;
using SilverModManager.Sdk.Resources;

namespace SilverModManager.UI.Tests.Pages;

public class ThumbnailLoaderTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly Uri IconUrl = new("https://gcdn.thunderstore.io/live/repository/icons/a-b-1.0.0.png");

    private sealed class FakePipeline : IResourceLoader<Uri, Bitmap>
    {
        public List<Uri> Requests { get; } = [];
        public Func<Uri, CancellationToken, Task<Bitmap>> Respond { get; set; } = (_, _) => throw new InvalidOperationException("no response set");

        public async ValueTask<Resource<Bitmap>> LoadResourceAsync(Uri resourceIdentifier, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(resourceIdentifier);
            return new Resource<Bitmap> { Data = await Respond(resourceIdentifier, cancellationToken) };
        }
    }

    private static Bitmap NewBitmap() => new RenderTargetBitmap(new PixelSize(4, 4));

    private static IImage Placeholder() => NewBitmap();

    [Fact]
    public async Task LoadAsync_ReturnsThePipelinesImageAndRemembersIt()
    {
        var image = NewBitmap();
        var pipeline = new FakePipeline { Respond = (_, _) => Task.FromResult(image) };
        var loader = new ThumbnailLoader(pipeline, Placeholder());

        loader.TryGetCached(IconUrl).Should().BeNull();
        (await loader.LoadAsync(IconUrl, CancellationToken.None)).Should().BeSameAs(image);
        (await loader.LoadAsync(IconUrl, CancellationToken.None)).Should().BeSameAs(image);

        pipeline.Requests.Should().ContainSingle();
        loader.TryGetCached(IconUrl).Should().BeSameAs(image);
    }

    [Fact]
    public async Task LoadAsync_WhenThePipelineFails_GivesThePlaceholderAndTriesAgainNextTime()
    {
        var placeholder = Placeholder();
        var image = NewBitmap();
        var fail = true;
        var pipeline = new FakePipeline { Respond = (_, _) => fail ? Task.FromException<Bitmap>(new HttpRequestException("offline")) : Task.FromResult(image) };
        var loader = new ThumbnailLoader(pipeline, placeholder);

        (await loader.LoadAsync(IconUrl, CancellationToken.None)).Should().BeSameAs(placeholder);
        loader.TryGetCached(IconUrl).Should().BeNull("a failure must not be remembered");

        fail = false;
        (await loader.LoadAsync(IconUrl, CancellationToken.None)).Should().BeSameAs(image);
    }

    [Fact]
    public async Task LoadAsync_ForANonHttpsUrl_GivesThePlaceholderWithoutAskingThePipeline()
    {
        var placeholder = Placeholder();
        var pipeline = new FakePipeline();
        var loader = new ThumbnailLoader(pipeline, placeholder);

        (await loader.LoadAsync(new Uri("file:///etc/passwd"), CancellationToken.None)).Should().BeSameAs(placeholder);
        (await loader.LoadAsync(new Uri("http://example.com/a.png"), CancellationToken.None)).Should().BeSameAs(placeholder);

        pipeline.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_WhenCancelled_ThrowsAndRemembersNothing()
    {
        var started = new TaskCompletionSource();
        var pipeline = new FakePipeline
        {
            Respond = async (_, token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return NewBitmap();
            },
        };
        var loader = new ThumbnailLoader(pipeline, Placeholder());
        using var cts = new CancellationTokenSource();

        var load = loader.LoadAsync(IconUrl, cts.Token);
        await started.Task;
        await cts.CancelAsync();

        await load.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        loader.TryGetCached(IconUrl).Should().BeNull();
    }

    [Fact]
    public async Task TheCacheIsBounded()
    {
        var pipeline = new FakePipeline { Respond = (_, _) => Task.FromResult(NewBitmap()) };
        var loader = new ThumbnailLoader(pipeline, Placeholder(), capacity: 8);

        for (var i = 0; i < 200; i++) await loader.LoadAsync(new Uri($"https://example.com/{i}.png"), CancellationToken.None);

        Enumerable.Range(0, 200).Count(i => loader.TryGetCached(new Uri($"https://example.com/{i}.png")) is not null).Should().BeLessThan(100);
    }
}
