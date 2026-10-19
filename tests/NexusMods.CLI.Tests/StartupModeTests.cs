using FluentAssertions;
using NexusMods.App;

namespace NexusMods.CLI.Tests;

public class StartupModeTests
{
    [Theory]
    [InlineData("", true, true, false, "")]
    [InlineData("as-main", true, false, false, "")]
    [InlineData("as-main-ui", true, true, false, "")]
    [InlineData("arg1 arg2", false, false, true, "arg1 arg2")]
    [InlineData("as-main arg1 arg2", true, false, true, "arg1 arg2")]
    [InlineData("as-main-ui arg1 arg2", true, true, true, "arg1 arg2")]
    public void TestStartupModeParsing(string input, bool asMain, bool showUi, bool executeCli, string finalArgs)
    {
        var splitInput = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var mode = StartupMode.Parse(splitInput);
        mode.RunAsMain.Should().Be(asMain);
        mode.ShowUI.Should().Be(showUi);
        mode.ExecuteCli.Should().Be(executeCli);
        string.Join(' ', mode.Args).Should().Be(finalArgs);
    }

    [Theory]
    [InlineData("--help", false)]
    [InlineData("loadouts list", false)]
    [InlineData("protocol-invoke -u nxm://stardewvalley/mods/1/files/2", true)]
    public void TestStartsUiWhenNoMainRunning(string input, bool expected)
    {
        var mode = StartupMode.Parse(input.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        // Only a client looks for a main process, so every row has to parse as one.
        mode.RunAsMain.Should().BeFalse();
        mode.StartsUiWhenNoMainRunning.Should().Be(expected);
    }

    [Theory]
    [InlineData("--help", "as-main --help")]
    [InlineData("loadouts list", "as-main loadouts list")]
    public void TestHeadlessMainArgsRoundTrip(string input, string expected)
    {
        var args = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var client = StartupMode.Parse(args);

        string.Join(' ', client.HeadlessMainArgs).Should().Be(expected);

        var main = StartupMode.Parse(client.HeadlessMainArgs);
        main.RunAsMain.Should().BeTrue();
        main.ShowUI.Should().BeFalse();
        main.ExecuteCli.Should().BeTrue();
        main.Args.Should().Equal(args);
    }
}
