using FluentAssertions;
using SilverModManager.Platform.Proton.Tools;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Platform.Proton.Tests.Tools;

/// <summary>
/// The text a player reads when a tool stops: what ran, how long, through what, and Wine's own words.
/// </summary>
public class ToolExitMessageTests
{
    [Fact]
    public void AWinePathSurvivesBothRenderers()
    {
        const string line = @"0024:err:module:import_dll Library X.dll (which is needed by L""Z:\home\me\RemapSkyrimVR_Oculus__v1.2\Remap.exe"") not found";
        var result = new ToolRunResult(1, TimeSpan.FromSeconds(2), "SteamProton", StderrTail: line);

        var message = ToolExitMessage.For("Remap Controls", result, hint: null);

        message.Should().Contain(line);
        MarkdownText.ToPlain(message).Should().Contain(@"Z:\home\me\RemapSkyrimVR_Oculus__v1.2\Remap.exe");
    }

    [Fact]
    public void TheSummaryNamesTheStrategyAndTheFallbackReason()
    {
        var result = new ToolRunResult(7, TimeSpan.FromMinutes(5), "Protontricks", "no Proton build was found");

        var message = ToolExitMessage.For("TexGen", result, hint: null);

        message.Should().StartWith("TexGen exited with code 7 after 5 min, run through Protontricks because no Proton build was found.");
    }

    [Theory]
    [InlineData(4, "4 s")]
    [InlineData(60, "1 min")]
    [InlineData(2400, "40 min")]
    public void DurationIsSecondsUnderAMinuteAndMinutesFromThere(int seconds, string expected)
        => ToolExitMessage.Duration(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
}
