using System.Collections.Concurrent;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SkiaSharp;

namespace SilverModManager.Backend.Games;

/// <summary>
/// The last link of the chain, which always answers: a flat tile in a colour taken from the game's id, with
/// the first letters of its name in white. Drawn in code, so the same game gets the same picture on every
/// machine and every start, and nothing is downloaded or shipped.
/// </summary>
internal sealed class InitialsArtSource : IGameArtProvider
{
    private const int TileWidth = 600;
    private const int TileHeight = 900;
    private const int IconSize = 256;

    // Light enough to be a colour, dark enough that white text clears 4.5:1 for every hue.
    private const float Saturation = 45;
    private const float Lightness = 30;

    private static readonly string[] SkippedWords = ["the", "of", "a", "an", "and"];

    // Games ask for their art on every page that shows them; the drawing only depends on the request.
    private readonly ConcurrentDictionary<(GameId, string, bool), byte[]> _rendered = new();

    public ValueTask<IStreamFactory?> FindTileAsync(GameArtRequest request) => Render(request, isIcon: false);

    public ValueTask<IStreamFactory?> FindIconAsync(GameArtRequest request) => Render(request, isIcon: true);

    /// <summary>One or two characters for the picture; "?" for a name with nothing to take.</summary>
    internal static string InitialsFor(string name)
    {
        // Split on whitespace only, so "Baldur's" stays one word; "&" and "II:" style tokens still work
        // because only the first letter or digit of each word is used.
        var words = name
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Any(char.IsLetterOrDigit))
            .ToArray();

        var significant = words.Where(word => !SkippedWords.Contains(word.ToLowerInvariant())).ToArray();
        var chosen = (significant.Length > 0 ? significant : words)
            .Take(2)
            .Select(word => char.ToUpperInvariant(word.First(char.IsLetterOrDigit)));
        var initials = string.Concat(chosen);
        return initials.Length == 0 ? "?" : initials;
    }

    /// <summary>A stable colour for the game: the hue comes from its id, saturation and lightness are fixed.</summary>
    internal static SKColor BackgroundFor(GameId id)
    {
        var value = id.Value;
        var hue = (value ^ (value >> 32)) % 360;
        return SKColor.FromHsl(hue, Saturation, Lightness);
    }

    private ValueTask<IStreamFactory?> Render(GameArtRequest request, bool isIcon)
    {
        // The drawing is quick, but it still has no business on the thread that asked for a picture.
        return new ValueTask<IStreamFactory?>(Task.Run<IStreamFactory?>(() =>
        {
            var png = _rendered.GetOrAdd((request.GameId, request.DisplayName, isIcon), key => Draw(key.Item1, key.Item2, key.Item3));
            return new MemoryStreamFactory(isIcon ? "icon.png" : "tile.png", new MemoryStream(png, writable: false));
        }));
    }

    private static byte[] Draw(GameId id, string name, bool isIcon)
    {
        var width = isIcon ? IconSize : TileWidth;
        var height = isIcon ? IconSize : TileHeight;

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(BackgroundFor(id));

        using var typeface = SKTypeface.FromFamilyName("sans-serif", SKFontStyle.Bold) ?? SKTypeface.Default;
        using var font = new SKFont(typeface, size: width * 0.4f) { Edging = SKFontEdging.SubpixelAntialias };
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };

        var text = InitialsFor(name);
        var textWidth = font.MeasureText(text);
        font.GetFontMetrics(out var metrics);
        var baseline = (height - (metrics.Ascent + metrics.Descent)) / 2;
        canvas.DrawText(text, (width - textWidth) / 2, baseline, SKTextAlign.Left, font, paint);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
