using FluentAssertions;
using SilverModManager.App;
using SilverModManager.CrossPlatform;

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

    [Theory]
    [InlineData("mcp", false, true)]
    [InlineData("mcp", true, true)]
    [InlineData("list-games", false, false)]
    [InlineData("list-games", true, true)]
    public void McpAlwaysLogsToTheConsole_EvenWhenTheSettingIsOff(string input, bool logToConsole, bool expected)
    {
        var mode = StartupMode.Parse(input.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var settings = new LoggingSettings { LogToConsole = logToConsole };

        // A Release build defaults the setting off, and the client's log is where a stdio server's problems get read.
        Program.WantsConsoleTarget(settings, mode).Should().Be(expected);
    }
}
