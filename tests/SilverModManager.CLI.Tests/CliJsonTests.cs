using System.Text.Json;
using FluentAssertions;
using SilverModManager.Abstractions.Cli;

namespace SilverModManager.CLI.Tests;

public class CliJsonTests
{
    [Fact]
    public void ApostropheAndNonAsciiLetterComeOutLiterally()
    {
        var json = JsonSerializer.Serialize(new { name = "Baldur's Gate 3 é" }, CliJson.Options);

        json.Should().Contain("Baldur's Gate 3 é");
        json.Should().NotContain("\\u");
    }

    [Fact]
    public void QuotesAreStillEscapedSoTheOutputStaysValidJson()
    {
        var json = JsonSerializer.Serialize(new { name = "say \"hi\"" }, CliJson.Options);

        JsonDocument.Parse(json).RootElement.GetProperty("name").GetString().Should().Be("say \"hi\"");
    }

    [Fact]
    public void OutputIsIndentedAndCamelCased()
    {
        var json = JsonSerializer.Serialize(new { SomeName = 1 }, CliJson.Options);

        json.Should().Contain("\n").And.Contain("\"someName\"");
    }
}
