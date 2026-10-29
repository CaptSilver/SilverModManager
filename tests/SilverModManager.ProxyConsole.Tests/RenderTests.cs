using FluentAssertions;
using SilverModManager.Sdk.ProxyConsole;
using Spectre.Console.Testing;

namespace SilverModManager.ProxyConsole.Tests;

public class RenderTests
{
    private readonly TestConsole _test;
    private readonly SpectreRenderer _renderer;

    public RenderTests()
    {
        _test = new TestConsole();
        _renderer = new SpectreRenderer(_test);

    }

    [Fact]
    public async Task CanRenderText()
    {
        await _renderer.RenderAsync(new Text {Template = "Hello World!"});

        _test.Output.Should().Be("Hello World!");
    }

    [Fact]
    public async Task CanRenderMultipleTexts()
    {
        await _renderer.RenderAsync(new Text {Template = "Hello World!1"});
        await _renderer.RenderAsync(new Text {Template = "Hello World!2"});
        await _renderer.RenderAsync(new Text {Template = "Hello World!3"});

        _test.Output.Should().Be("Hello World!1Hello World!2Hello World!3");
    }

    [Fact]
    public async Task TextWithBracesAndNoArgumentsIsRenderedVerbatim()
    {
        var definition = new RenderDefinitions.TextRenderDefinition();

        var rendered = await definition.ToSpectreAsync(new Text { Template = "C:\\mods\\{abc}.json {\"a\": 1}" }, _ => throw new InvalidOperationException());

        var console = new TestConsole();
        console.Write(rendered);
        console.Output.Should().Be("C:\\mods\\{abc}.json {\"a\": 1}");
    }

    [Fact]
    public async Task TextWithArgumentsIsFormatted()
    {
        var definition = new RenderDefinitions.TextRenderDefinition();

        var rendered = await definition.ToSpectreAsync(new Text { Template = "{0} of {1}", Arguments = ["1", "2"] }, _ => throw new InvalidOperationException());

        var console = new TestConsole();
        console.Write(rendered);
        console.Output.Should().Be("1 of 2");
    }

    [Fact]
    public async Task LongJsonLine_IsNotWrappedByTheRenderer()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { id = 1, message = new string('a', 60) + " " + new string('b', 60) + " and some more words that go past eighty columns" },
        });
        var console = new TestConsole();
        console.Profile.Width = 80;
        var renderer = new SpectreRenderer(console);

        await renderer.RenderAsync(new Text { Template = json + "\n" });

        console.Output.Should().Be(json + "\n");
        System.Text.Json.JsonDocument.Parse(console.Output).RootElement.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task LongJsonLine_IsNotWrappedByTheTextRenderDefinition()
    {
        var json = "{\"message\": \"" + string.Join(' ', Enumerable.Repeat("word", 40)) + "\"}\n";
        var rendered = await new RenderDefinitions.TextRenderDefinition().ToSpectreAsync(new Text { Template = json }, _ => throw new InvalidOperationException());
        var console = new TestConsole();
        console.Profile.Width = 80;

        console.Write(rendered);

        console.Output.Should().Be(json);
    }
}
