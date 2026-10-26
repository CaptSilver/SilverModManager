using FluentAssertions;
using SilverModManager.App;

namespace SilverModManager.CLI.Tests;

public class ConsoleLogTargetTests
{
    [Theory]
    [InlineData("mcp", true)]
    [InlineData("mcp --read-only", true)]
    [InlineData("list-games", false)]
    [InlineData("", false)]
    public void OnlyMcpWritesItsConsoleLogToStderr(string input, bool stdErr)
    {
        var mode = StartupMode.Parse(input.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        // stdout carries the protocol in mcp mode, so a log line there would corrupt a frame.
        Program.CreateConsoleTarget(mode).StdErr.Should().Be(stdErr);
    }
}
