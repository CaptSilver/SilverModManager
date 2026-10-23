using FluentAssertions;
using SilverModManager.Wabbajack.Mapping;

namespace SilverModManager.Wabbajack.Tests.Mapping;

public class PathMarkersTests
{
    private static readonly PathMarkerValues Windows = new(@"Z:\stub\game", @"Z:\stub\mo2", @"Z:\stub\dl");
    private static readonly string AllNine =
        "{--||GAME_PATH_MAGIC_BACK||--} {--||GAME_PATH_MAGIC_DOUBLE_BACK||--} {--||GAME_PATH_MAGIC_FORWARD||--} "
        + "{--||MO2_PATH_MAGIC_BACK||--} {--||MO2_PATH_MAGIC_DOUBLE_BACK||--} {--||MO2_PATH_MAGIC_FORWARD||--} "
        + "{--||DOWNLOAD_PATH_MAGIC_BACK||--} {--||DOWNLOAD_PATH_MAGIC_DOUBLE_BACK||--} {--||DOWNLOAD_PATH_MAGIC_FORWARD||--}";

    [Fact]
    public void AllNineMarkers_GetTheirWindowsSpelling()
    {
        PathMarkers.Substitute(AllNine, Windows, nativeSpelling: false).Should().Be(
            @"Z:\stub\game Z:\\stub\\game Z:/stub/game "
            + @"Z:\stub\mo2 Z:\\stub\\mo2 Z:/stub/mo2 "
            + @"Z:\stub\dl Z:\\stub\\dl Z:/stub/dl");
    }

    [Fact]
    public void NativeSpelling_UsesTheHostPath_AndTurnsTheBackslashesAfterAMarkerIntoSlashes()
    {
        var fixtureArguments = "\"{--||GAME_PATH_MAGIC_DOUBLE_BACK||--}\\data\"";

        PathMarkers.Substitute(fixtureArguments, new PathMarkerValues("/stub/game", "/stub/mo2", "/stub/dl"), nativeSpelling: true)
            .Should().Be("\"/stub/game/data\"");
    }

    [Fact]
    public void NativeSpelling_LeavesBackslashesOutsideTheMarkersTokenAlone()
    {
        var values = new PathMarkerValues("/stub/game", "/stub/mo2", "/stub/dl");

        PathMarkers.Substitute(@"-a C:\keep {--||MO2_PATH_MAGIC_BACK||--}\tools\x.exe -b D:\keep", values, nativeSpelling: true)
            .Should().Be(@"-a C:\keep /stub/mo2/tools/x.exe -b D:\keep");
    }

    [Fact]
    public void TextWithoutMarkers_IsReturnedAsItWas()
    {
        PathMarkers.Substitute("a{b}c", Windows, nativeSpelling: false).Should().Be("a{b}c");
    }
}
