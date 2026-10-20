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
    [InlineData("run", true, false, false, "")]
    [InlineData("run 12345", true, false, false, "12345")]
    [InlineData("run -- game.exe --flag", true, false, false, "-- game.exe --flag")]
    [InlineData("run --help", false, false, false, "--help")]
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
    [InlineData("run", true)]
    [InlineData("RUN 12345", true)]
    [InlineData("", false)]
    [InlineData("as-main run", false)]
    [InlineData("run --help", false)]
    [InlineData("run -h", false)]
    [InlineData("run Skyrim -- game.exe --help", true)]
    [InlineData("loadouts list", false)]
    [InlineData("steam-launch-option", false)]
    public void TestIsHeadlessRun(string input, bool expected)
    {
        var mode = StartupMode.Parse(input.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        mode.IsHeadlessRun.Should().Be(expected);
    }

    [Theory]
    [InlineData("status", true)]
    [InlineData("STATUS --json", true)]
    [InlineData("", false)]
    [InlineData("run", false)]
    [InlineData("as-main status", false)]
    [InlineData("loadouts list", false)]
    public void TestIsStatus(string input, bool expected)
    {
        var mode = StartupMode.Parse(input.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        mode.IsStatus.Should().Be(expected);
    }

    [Theory]
    [InlineData("run --help", true)]
    [InlineData("run -h", true)]
    [InlineData("RUN --HELP", false)]
    [InlineData("run Skyrim --help", true)]
    [InlineData("run Skyrim -- game.exe --help", false)]
    [InlineData("run Skyrim -- game.exe -h", false)]
    [InlineData("run -- --help", false)]
    [InlineData("run", false)]
    [InlineData("--help", false)]
    [InlineData("as-main run --help", false)]
    public void TestIsRunHelp(string input, bool expected)
    {
        var mode = StartupMode.Parse(input.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        mode.IsRunHelp.Should().Be(expected);
    }

    [Fact]
    public void RunHelpBuildsNoHostAndShowsNoUi()
    {
        var mode = StartupMode.Parse(["run", "--help"]);

        mode.IsHeadlessRun.Should().BeFalse();
        mode.RunAsMain.Should().BeFalse();
        mode.ShowUI.Should().BeFalse();
        mode.ExecuteCli.Should().BeFalse();
    }

    [Fact]
    public void StatusBuildsNoHostAndShowsNoUi()
    {
        var mode = StartupMode.Parse(["status", "--json"]);

        mode.RunAsMain.Should().BeFalse();
        mode.ShowUI.Should().BeFalse();
        mode.ExecuteCli.Should().BeFalse();
        mode.Args.Should().Equal("--json");
    }

    [Theory]
    [InlineData("--help", false)]
    [InlineData("loadouts list", false)]
    [InlineData("protocol-invoke -u nxm://stardewvalley/mods/1/files/2", true)]
    [InlineData("steam-launch-option", false)]
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
    [InlineData("steam-launch-option --app 1091500", "as-main steam-launch-option --app 1091500")]
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

    [Theory]
    [InlineData("selftest-helper ping", true)]
    [InlineData("SELFTEST-HELPER hold-lock /x 1", true)]
    [InlineData("", false)]
    [InlineData("as-main selftest-helper ping", false)]
    [InlineData("status", false)]
    public void TestIsSelftestHelper(string input, bool expected)
    {
        var mode = StartupMode.Parse(input.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        mode.IsSelftestHelper.Should().Be(expected);
    }

    [Fact]
    public void SelftestHelperBuildsNoHostAndShowsNoUi()
    {
        var mode = StartupMode.Parse(["selftest-helper", "hold-lock", "/x", "5"]);

        mode.RunAsMain.Should().BeFalse();
        mode.ShowUI.Should().BeFalse();
        mode.ExecuteCli.Should().BeFalse();
        mode.IsHeadlessRun.Should().BeFalse();
        mode.Args.Should().Equal("hold-lock", "/x", "5");
    }
}
