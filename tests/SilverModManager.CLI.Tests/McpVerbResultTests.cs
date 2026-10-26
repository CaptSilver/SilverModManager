using FluentAssertions;
using SilverModManager.App.Mcp;

namespace SilverModManager.CLI.Tests;

/// <summary>How the exit code a verb ended with, and the text it printed, become a tool result.</summary>
public sealed class McpVerbResultTests
{
    private static readonly VerbCall Plain = new(TimeSpan.FromSeconds(1), ReportsExitOne: false, MaxChars: 1_000);
    private static readonly VerbCall Report = Plain with { ReportsExitOne = true };

    [Fact]
    public void ExitZero_IsTheText() =>
        VerbResults.Map(0, "hello\n", Plain).Should().Be(new ToolResult("hello\n", false));

    [Fact]
    public void ExitZeroWithNoText_UsesTheCallsEmptyText() =>
        VerbResults.Map(0, "", Plain with { EmptyText = "Applied x." }).Should().Be(new ToolResult("Applied x.", false));

    [Fact]
    public void ExitOneFromAReportTool_KeepsTheText_AndAddsTheCode() =>
        VerbResults.Map(1, "{\"refused\":true}\n", Report).Should().Be(new ToolResult("{\"refused\":true}\n\nexit code 1", false));

    [Fact]
    public void ExitOneFromAnyOtherTool_IsAnErrorWithTheTextOnly() =>
        VerbResults.Map(1, "Nope.\n", Plain).Should().Be(new ToolResult("Nope.\n", true));

    [Fact]
    public void ExitMinusOne_IsAnErrorEvenFromAReportTool() =>
        VerbResults.Map(-1, "Details are in the log at /x.\n", Report).Should().Be(new ToolResult("Details are in the log at /x.\n", true));

    [Fact]
    public void AFailureThatPrintedNothing_StillSaysWhatHappened()
    {
        var result = VerbResults.Map(1, "", Plain);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("exit code 1");
    }

    [Fact]
    public void NoExitCode_MeansTheConnectionClosedBeforeTheCommandFinished_AndKeepsTheOutputSoFar()
    {
        var result = VerbResults.Map(null, "partial", Plain);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("partial").And.Contain("closed the connection before the command finished");
    }

    [Fact]
    public void TextOverTheCap_IsCut_AndSaysHowMuchWasKept()
    {
        var result = VerbResults.Map(0, new string('x', 5_000), Plain with { MaxChars = 100 });

        result.Text.Should().StartWith(new string('x', 100)).And.Contain("cut at 100 characters");
        result.Text.Length.Should().BeLessThan(300);
    }
}
