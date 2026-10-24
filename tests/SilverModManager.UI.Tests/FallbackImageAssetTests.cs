using Avalonia.Platform;
using FluentAssertions;
using SkiaSharp;

namespace SilverModManager.UI.Tests;

/// <summary>
/// The fallback images are PNGs drawn outside the build, so a bad export would only show up as a blank
/// or wrong-sized tile once someone opens a mod or collection without artwork.
/// </summary>
public class FallbackImageAssetTests : AUiTest
{
    public FallbackImageAssetTests(IServiceProvider provider) : base(provider) { }

    // The gray the placeholder glyph is drawn in.
    private static readonly SKColor GlyphGray = new(0xA1, 0xA1, 0xAA);

    [Theory]
    [InlineData("mod-thumbnail-fallback.png", 90, 56)]
    [InlineData("collection-tile-fallback.png", 144, 180)]
    [InlineData("collection-background-fallback.png", 100, 100)]
    public void Test_FallbackAssetDecodesAtItsExpectedSize(string name, int width, int height)
    {
        using var bitmap = Decode(name);
        bitmap.Width.Should().Be(width);
        bitmap.Height.Should().Be(height);
    }

    [Theory]
    [InlineData("mod-thumbnail-fallback.png", 34)]
    [InlineData("collection-tile-fallback.png", 64)]
    public void Test_TileFallbackShowsThePackageGlyph(string name, int glyphSize)
    {
        using var bitmap = Decode(name);

        // The glyph is a centred square; the left edge of the box outline is solid around (4, 12) on the
        // 24-unit icon grid. The old placeholder (a pinwheel logo) left this spot empty.
        var unit = glyphSize / 24f;
        var x = (int)((bitmap.Width - glyphSize) / 2f + 4 * unit);
        var y = (int)((bitmap.Height - glyphSize) / 2f + 12 * unit);
        bitmap.GetPixel(x, y).Should().Be(GlyphGray);

        // Tile corners stay transparent so the rounded outline survives.
        bitmap.GetPixel(0, 0).Alpha.Should().Be(0);
    }

    private static SKBitmap Decode(string name)
    {
        using var stream = AssetLoader.Open(new Uri(new Uri("avares://SilverModManager.App.UI/"), $"Assets/{name}"));
        return SKBitmap.Decode(stream) ?? throw new InvalidOperationException($"{name} did not decode");
    }
}
