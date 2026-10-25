using Avalonia.Media.Imaging;
using FluentAssertions;
using SilverModManager.App.UI;
using SkiaSharp;
using System.Net;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// The Wabbajack gallery's pictures are mostly webp. The card thumbnails go through the shared thumbnail pipeline, so
/// it has to decode them; a pipeline that couldn't would show every card blank.
/// </summary>
public class ThumbnailWebpTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class FixedBody(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    private static byte[] Webp(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Teal);
        using var data = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Webp, 80);
        return data.ToArray();
    }

    [Fact]
    public async Task TheThumbnailPipeline_DecodesWebpAndKeepsTheAspectRatio()
    {
        var bytes = Webp(1280, 720);
        bytes.AsSpan(8, 4).ToArray().Should().Equal("WEBP"u8.ToArray(), "the fixture has to really be webp");
        using var client = new HttpClient(new FixedBody(bytes));
        var pipeline = ImagePipelines.CreateThumbnailPipeline(client);

        var resource = await pipeline.LoadResourceAsync(new Uri("https://example.invalid/list.webp"), CancellationToken.None);

        resource.Data.Should().BeOfType<Bitmap>();
        resource.Data.Size.Width.Should().BeGreaterThan(0);
        (resource.Data.Size.Width / resource.Data.Size.Height).Should().BeApproximately(16.0 / 9.0, 0.05);
    }
}
