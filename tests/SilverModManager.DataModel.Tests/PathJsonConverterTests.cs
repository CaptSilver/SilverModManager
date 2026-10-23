using System.Text.Json;
using FluentAssertions;
using SilverModManager.DataModel.JsonConverters;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.DataModel.Tests;

public class PathJsonConverterTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new GamePathConverter(), new AbsolutePathConverter(FileSystem.Shared) },
    };

    [Fact]
    public void GamePathRoundTrips()
    {
        var path = new GamePath(LocationId.Game, "Data/plugin.esm");

        JsonSerializer.Deserialize<GamePath>(JsonSerializer.Serialize(path, Options), Options).Should().Be(path);
    }

    [Fact]
    public void GamePathWithANullPathIsAJsonError()
    {
        var act = () => JsonSerializer.Deserialize<GamePath>("""["Game", null]""", Options);

        act.Should().Throw<JsonException>().WithMessage("*path*");
    }

    [Fact]
    public void AbsolutePathFromJsonNullIsAJsonError()
    {
        var act = () => JsonSerializer.Deserialize<AbsolutePath>("null", Options);

        act.Should().Throw<JsonException>().WithMessage("*path*");
    }
}
