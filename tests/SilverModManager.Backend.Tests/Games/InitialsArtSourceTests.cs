using FluentAssertions;
using SilverModManager.Backend.Games;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SkiaSharp;

namespace SilverModManager.Backend.Tests.Games;

public class InitialsArtSourceTests
{
    private static GameArtRequest Request(string id, string name) => new(GameId.From(id), name, []);

    private static async Task<byte[]> ReadAll(IStreamFactory? factory)
    {
        factory.Should().NotBeNull();
        await using var stream = await factory!.GetStreamAsync();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }

    [Theory]
    [InlineData("Stardew Valley", "SV")]
    [InlineData("Cyberpunk 2077", "C2")]
    [InlineData("Baldur's Gate 3", "BG")]
    [InlineData("Mount & Blade II: Bannerlord", "MB")]
    [InlineData("The Witcher 3: Wild Hunt", "W3")]
    [InlineData("Skyrim", "S")]
    [InlineData("  elden   ring ", "ER")]
    [InlineData("Dragon Age: The Veilguard", "DA")]
    [InlineData("", "?")]
    [InlineData("   ", "?")]
    public void InitialsFor_TakesTheFirstLettersOfTheFirstTwoWords(string name, string expected)
        => InitialsArtSource.InitialsFor(name).Should().Be(expected);

    [Fact]
    public async Task Tile_SameGame_IsByteForByteTheSame()
    {
        var first = await ReadAll(await new InitialsArtSource().FindTileAsync(Request("A.Game", "Alpha Beta")));
        var second = await ReadAll(await new InitialsArtSource().FindTileAsync(Request("A.Game", "Alpha Beta")));

        second.Should().Equal(first);
    }

    [Fact]
    public async Task Tile_DifferentGames_Differ()
    {
        var source = new InitialsArtSource();
        var alpha = await ReadAll(await source.FindTileAsync(Request("A.Game", "Alpha Beta")));
        var other = await ReadAll(await source.FindTileAsync(Request("B.Game", "Gamma Delta")));

        other.Should().NotEqual(alpha);
    }

    [Fact]
    public async Task Tile_SameInitialsDifferentIds_DifferInColour()
    {
        var source = new InitialsArtSource();
        var first = await ReadAll(await source.FindTileAsync(Request("Studio.One", "Alpha Beta")));
        var second = await ReadAll(await source.FindTileAsync(Request("Studio.Two", "Alpha Beta")));

        second.Should().NotEqual(first);
    }

    [Fact]
    public async Task Tile_IsAPortraitPng_AndIconIsASquarePng()
    {
        var source = new InitialsArtSource();
        var request = Request("A.Game", "Alpha Beta");

        using var tile = SKBitmap.Decode(await ReadAll(await source.FindTileAsync(request)));
        using var icon = SKBitmap.Decode(await ReadAll(await source.FindIconAsync(request)));

        (tile.Width, tile.Height).Should().Be((600, 900));
        (icon.Width, icon.Height).Should().Be((256, 256));
    }

    [Fact]
    public async Task Tile_CornerPixelIsTheColourDerivedFromTheId()
    {
        var request = Request("A.Game", "Alpha Beta");

        using var tile = SKBitmap.Decode(await ReadAll(await new InitialsArtSource().FindTileAsync(request)));

        tile.GetPixel(2, 2).Should().Be(InitialsArtSource.BackgroundFor(request.GameId));
    }

    [Fact]
    public void BackgroundFor_IsDarkEnoughForWhiteText()
    {
        foreach (var id in Enumerable.Range(0, 500).Select(i => GameId.From($"Game.{i}")))
        {
            var colour = InitialsArtSource.BackgroundFor(id);
            // WCAG relative luminance; white text needs a ratio of at least 4.5 against the background.
            var ratio = 1.05 / (Luminance(colour) + 0.05);
            ratio.Should().BeGreaterThanOrEqualTo(4.5, $"{id} gave {colour}");
        }
    }

    [Fact]
    public async Task NoSteamIds_StillAnswers()
    {
        var tile = await new InitialsArtSource().FindTileAsync(Request("A.Game", "Alpha Beta"));

        tile.Should().NotBeNull();
    }

    private static double Luminance(SKColor colour)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(colour.Red) + 0.7152 * Channel(colour.Green) + 0.0722 * Channel(colour.Blue);
    }
}
