using FluentAssertions;
using SilverModManager.App.Mcp;
using SilverModManager.Sdk.ProxyConsole;
using Table = SilverModManager.Sdk.ProxyConsole.Table;

namespace SilverModManager.CLI.Tests;

public class CapturingRendererTests
{
    private static Text Plain(string template, params string[] arguments) => new() { Template = template, Arguments = arguments };

    [Fact]
    public async Task TextWithoutArguments_IsKeptVerbatim_SoJsonBracesSurvive()
    {
        const string json = "{\"name\":\"a\",\"list\":[{\"id\":1}]}";
        var renderer = new CapturingRenderer();

        await renderer.RenderAsync(Plain(json));

        renderer.Output.Should().Be(json);
    }

    [Fact]
    public async Task TextWithArguments_IsFormatted()
    {
        var renderer = new CapturingRenderer();

        await renderer.RenderAsync(Plain("Loadout {0} has {1} mods", "Main", "12"));

        renderer.Output.Should().Be("Loadout Main has 12 mods");
    }

    [Fact]
    public async Task SeveralTexts_AreJoinedWithoutAddedSeparators()
    {
        var renderer = new CapturingRenderer();

        await renderer.RenderAsync(Plain("one\n"));
        await renderer.RenderAsync(Plain("two"));

        renderer.Output.Should().Be("one\ntwo");
    }

    [Fact]
    public async Task ATable_BecomesAMarkdownTable()
    {
        var renderer = new CapturingRenderer();

        await renderer.RenderAsync(new Table
        {
            Columns = [Plain("Name"), Plain("Count")],
            Rows = [[Plain("{0}", "Main"), Plain("3")]],
        });

        renderer.Output.Should().Be("| Name | Count |\n| --- | --- |\n| Main | 3 |\n");
    }

    [Fact]
    public async Task ATableCell_WithAPipeAndANewline_StaysInItsCell()
    {
        var renderer = new CapturingRenderer();

        await renderer.RenderAsync(new Table
        {
            Columns = [Plain("Note")],
            Rows = [[Plain("a|b\nc")]],
        });

        renderer.Output.Should().Be("| Note |\n| --- |\n| a\\|b c |\n");
    }

    [Fact]
    public async Task ATableAfterUnterminatedText_StartsOnALineOfItsOwn()
    {
        var renderer = new CapturingRenderer();

        await renderer.RenderAsync(Plain("Games"));
        await renderer.RenderAsync(new Table { Columns = [Plain("Name")], Rows = [] });

        renderer.Output.Should().Be("Games\n| Name |\n| --- |\n");
    }

    [Fact]
    public async Task Clear_DoesNotDropEarlierText()
    {
        var renderer = new CapturingRenderer();

        await renderer.RenderAsync(Plain("before"));
        await renderer.ClearAsync();
        await renderer.RenderAsync(Plain(" after"));

        renderer.Output.Should().Be("before after");
    }

    [Fact]
    public async Task ProgressRenderables_AreIgnored()
    {
        var renderer = new CapturingRenderer();

        await renderer.RenderAsync(new StartProgress());
        await renderer.RenderAsync(Plain("done"));
        await renderer.RenderAsync(new StopProgress());

        renderer.Output.Should().Be("done");
    }
}
