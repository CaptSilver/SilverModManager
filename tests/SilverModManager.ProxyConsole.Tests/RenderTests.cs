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

    private static Table LongCellTable() => new()
    {
        Columns = [new Text { Template = "Name" }, new Text { Template = "Description" }],
        Rows = [[new Text { Template = "a" }, new Text { Template = string.Join(' ', Enumerable.Repeat("word", 30)) }]],
    };

    [Fact]
    public async Task LongTableCell_StillWrapsInTheSpectreRenderer()
    {
        var console = new TestConsole();
        console.Profile.Width = 40;

        await new SpectreRenderer(console).RenderAsync(LongCellTable());

        console.Lines.Should().OnlyContain(l => l.Length <= 40);
        console.Lines.Count.Should().BeGreaterThan(5);
    }

    [Fact]
    public async Task LongTableCell_StillWrapsInTheTableRenderDefinition()
    {
        var text = new RenderDefinitions.TextRenderDefinition();
        var rendered = await new RenderDefinitions.TableRenderDefinition().ToSpectreAsync(LongCellTable(),
            r => text.ToSpectreAsync(r, _ => throw new InvalidOperationException()));
        var console = new TestConsole();
        console.Profile.Width = 40;

        console.Write(rendered);

        console.Lines.Should().OnlyContain(l => l.Length <= 40);
        console.Lines.Count.Should().BeGreaterThan(5);
    }

    private const string Hostile = "a\u001b]0;pwned\u0007b\u001b[2Jc\u009b2Jd\u202Ee\u0000f";

    [Fact]
    public async Task TextLine_DropsTerminalControlSequences()
    {
        await _renderer.RenderAsync(new Text { Template = $"line1\n\t{Hostile}\r\nÜnïcödé 日本" });

        _test.Output.Should().Be("line1\n\ta]0;pwnedb[2Jc2Jdef\nÜnïcödé 日本");
    }

    [Fact]
    public async Task TextArguments_DropTerminalControlSequences()
    {
        await _renderer.RenderAsync(new Text { Template = "name: {0}", Arguments = [Hostile] });

        _test.Output.Should().Be("name: a]0;pwnedb[2Jc2Jdef");
    }

    [Fact]
    public async Task TableCell_DropsTerminalControlSequences()
    {
        var console = new TestConsole();
        console.Profile.Width = 80;

        await new SpectreRenderer(console).RenderAsync(new Table
        {
            Columns = [new Text { Template = "Name" }],
            Rows = [[new Text { Template = Hostile }], [new Text { Template = "Ünïcödé 日本" }]],
        });

        console.Output.Should().Contain("a]0;pwnedb[2Jc2Jdef").And.Contain("Ünïcödé 日本");
        console.Output.Should().NotContainAny("\u001b", "\u0007", "\u009b", "\u202E", "\0");
    }

    [Fact]
    public async Task TableCell_DropsTerminalControlSequences_InTheTableRenderDefinition()
    {
        var text = new RenderDefinitions.TextRenderDefinition();
        var rendered = await new RenderDefinitions.TableRenderDefinition().ToSpectreAsync(
            new Table { Columns = [new Text { Template = "Name" }], Rows = [[new Text { Template = Hostile }]] },
            r => text.ToSpectreAsync(r, _ => throw new InvalidOperationException()));
        var console = new TestConsole();

        console.Write(rendered);

        console.Output.Should().Contain("a]0;pwnedb[2Jc2Jdef");
        console.Output.Should().NotContainAny("\u001b", "\u0007", "\u009b", "\u202E", "\0");
    }

    [Fact]
    public async Task SquareBracketsInValues_AreStillShownLiterally()
    {
        await _renderer.RenderAsync(new Text { Template = "{0}", Arguments = ["[red]x[/]"] });

        _test.Output.Should().Be("[red]x[/]");
    }

    [Theory]
    [InlineData("x\ry", "name: x y")]
    [InlineData("x\ny", "name: x y")]
    [InlineData("x\ty", "name: x y")]
    public async Task TextArguments_FlattenLineBreaksAndTabs(string value, string expected)
    {
        await _renderer.RenderAsync(new Text { Template = "name: {0}", Arguments = [value] });

        _test.Output.Should().Be(expected);
    }

    [Fact]
    public async Task TableCell_DropsACarriageReturn()
    {
        var console = new TestConsole();
        console.Profile.Width = 80;

        await new SpectreRenderer(console).RenderAsync(new Table
        {
            Columns = [new Text { Template = "Name" }],
            Rows = [[new Text { Template = "a\rb" }], [new Text { Template = "{0}", Arguments = ["c\rd"] }]],
        });

        console.Output.Should().NotContain("\r").And.Contain("ab").And.Contain("c d");
    }

    [Fact]
    public async Task TextLine_DropsFormatCharactersOutsideTheBasicMultilingualPlane()
    {
        await _renderer.RenderAsync(new Text { Template = "n: {0}|{1}", Arguments = ["a\U000E0041b\U000E0001", "c\uD800d"] });

        _test.Output.Should().Be("n: ab|cd");
    }
}
