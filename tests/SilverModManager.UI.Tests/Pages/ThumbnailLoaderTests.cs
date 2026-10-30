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

    /// <summary>A bitmap that was disposed has no platform image left; reading its size is how Avalonia's renderer found out.</summary>
    private static void AssertUsable(IImage image) => image.Invoking(i => _ = i.Size).Should().NotThrow("an image a card may still draw must not be disposed");

    [Fact]
    public async Task AnImageThatLeavesTheCache_IsNotDisposed()
    {
        var pipeline = new FakePipeline { Respond = (_, _) => Task.FromResult(NewBitmap()) };
        var loader = new ThumbnailLoader(pipeline, Placeholder(), capacity: 8);
        var shown = await loader.LoadAsync(new Uri("https://example.com/shown.png"), CancellationToken.None);

        for (var i = 0; i < 200; i++) await loader.LoadAsync(new Uri($"https://example.com/{i}.png"), CancellationToken.None);

        loader.TryGetCached(new Uri("https://example.com/shown.png")).Should().BeNull("the image was pushed out");
        AssertUsable(shown);
    }

    [Fact]
    public async Task TwoLoadsOfOneUrl_ShareTheFirstImageAndNeitherIsDisposed()
    {
        var gate = new TaskCompletionSource();
        var pipeline = new FakePipeline { Respond = async (_, _) => { await gate.Task; return NewBitmap(); } };
        var loader = new ThumbnailLoader(pipeline, Placeholder());

        var first = loader.LoadAsync(IconUrl, CancellationToken.None);
        var second = loader.LoadAsync(IconUrl, CancellationToken.None);
        gate.SetResult();
        var images = await Task.WhenAll(first, second);

        images[1].Should().BeSameAs(images[0]);
        AssertUsable(images[0]);
        AssertUsable(images[1]);
    }

    [Fact]
    public async Task RenderingAnImage_AfterItWasPushedOutOfTheCache_DoesNotThrow()
    {
        var pipeline = new FakePipeline { Respond = (_, _) => Task.FromResult(NewBitmap()) };
        var loader = new ThumbnailLoader(pipeline, Placeholder(), capacity: 8);
        var shown = await loader.LoadAsync(new Uri("https://example.com/shown.png"), CancellationToken.None);
        var control = await OnUi(() =>
        {
            var image = new Avalonia.Controls.Image { Source = shown, Width = 4, Height = 4 };
            image.Measure(new Size(4, 4));
            image.Arrange(new Rect(0, 0, 4, 4));
            return Task.FromResult(image);
        });

        for (var i = 0; i < 200; i++) await loader.LoadAsync(new Uri($"https://example.com/{i}.png"), CancellationToken.None);

        await OnUi(() =>
        {
            using var target = new RenderTargetBitmap(new PixelSize(4, 4));
            target.Invoking(t => t.Render(control)).Should().NotThrow();
            return Task.CompletedTask;
        });
    }
}
