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

    private static readonly PathMarkerValues WithStock = new(@"Z:\stub\game", @"Z:\stub\mo2", @"Z:\stub\dl", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Game Root" });

    [Fact]
    public void Mo2PathFollowedByTheStockGameFolder_ResolvesToTheGameFolder_InWindowsSpelling()
    {
        PathMarkers.Substitute(@"gamePath={--||MO2_PATH_MAGIC_BACK||--}\Game Root", WithStock, nativeSpelling: false)
            .Should().Be(@"gamePath=Z:\stub\game");
        PathMarkers.Substitute(@"{--||MO2_PATH_MAGIC_BACK||--}\Game Root\SKSE\x.exe", WithStock, nativeSpelling: false)
            .Should().Be(@"Z:\stub\game\SKSE\x.exe");
    }

    [Fact]
    public void Mo2PathFollowedByTheStockGameFolder_ResolvesToTheGameFolder_InNativeSpelling()
    {
        var values = new PathMarkerValues("/stub/game", "/stub/mo2", "/stub/dl", new HashSet<string> { "Game Root" });

        PathMarkers.Substitute(@"{--||MO2_PATH_MAGIC_BACK||--}\Game Root\SKSE\x.exe", values, nativeSpelling: true)
            .Should().Be("/stub/game/SKSE/x.exe");
    }

    [Fact]
    public void DoubleBackAndForwardSpellings_AlsoResolveTheStockGameFolder()
    {
        PathMarkers.Substitute(@"{--||MO2_PATH_MAGIC_DOUBLE_BACK||--}\\Game Root\\data", WithStock, nativeSpelling: false)
            .Should().Be(@"Z:\\stub\\game\\data");
        PathMarkers.Substitute("{--||MO2_PATH_MAGIC_FORWARD||--}/Game Root/data", WithStock, nativeSpelling: false)
            .Should().Be("Z:/stub/game/data");
    }

    [Fact]
    public void AStockFolderWithAnotherName_IsMatchedByItsName_AndTheOneTheListDoesNotHaveIsNot()
    {
        var values = new PathMarkerValues(@"Z:\g", @"Z:\m", @"Z:\d", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Stock Game" });

        PathMarkers.Substitute(@"{--||MO2_PATH_MAGIC_BACK||--}\stock game\a", values, nativeSpelling: false).Should().Be(@"Z:\g\a");
        PathMarkers.Substitute(@"{--||MO2_PATH_MAGIC_BACK||--}\Game Root\a", values, nativeSpelling: false).Should().Be(@"Z:\m\Game Root\a");
    }

    [Fact]
    public void AListWithoutAStockGameFolder_IsUnchanged()
    {
        PathMarkers.Substitute(@"{--||MO2_PATH_MAGIC_BACK||--}\Game Root\a", Windows, nativeSpelling: false)
            .Should().Be(@"Z:\stub\mo2\Game Root\a");
    }

    [Fact]
    public void APathUnderMo2ThatIsNotTheStockFolder_StillMapsToTheToolsFolder()
    {
        PathMarkers.Substitute(@"{--||MO2_PATH_MAGIC_BACK||--}\tools\x.exe {--||MO2_PATH_MAGIC_BACK||--}\Game Root2\x {--||MO2_PATH_MAGIC_BACK||--}", WithStock, nativeSpelling: false)
            .Should().Be(@"Z:\stub\mo2\tools\x.exe Z:\stub\mo2\Game Root2\x Z:\stub\mo2");
    }
}
