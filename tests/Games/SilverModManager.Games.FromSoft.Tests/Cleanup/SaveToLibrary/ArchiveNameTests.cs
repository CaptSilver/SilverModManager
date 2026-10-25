using FluentAssertions;
using SilverModManager.Games.FromSoft.Cleanup.SaveToLibrary;

namespace SilverModManager.Games.FromSoft.Tests.Cleanup.SaveToLibrary;

public class ArchiveNameTests
{
    [Fact]
    public void AnOldStyleNameGivesTheModIdAndTheUploadTimeInSeconds()
    {
        var name = ArchiveName.TryParse("FixTheCamera-118-3-0-1652303340.zip", "3.0");

        name.Should().NotBeNull();
        name!.Name.Should().Be("FixTheCamera");
        name.ModId.Should().Be(118u);
        name.Uploaded.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1652303340));
        name.MinuteResolution.Should().BeFalse();
    }

    [Fact]
    public void TheModIdIsTheNumberWhoseFollowingTextIsTheVersion()
    {
        // "2" and "0" also sit between dashes; only 2790 is followed by the version 2.0.1.
        var name = ArchiveName.TryParse("Boss Resurrection - Lite-2790-2-0-1-1720450846.zip", "2.0.1");

        name!.ModId.Should().Be(2790u);
        name.Name.Should().Be("Boss Resurrection - Lite");
    }

    [Fact]
    public void AVersionWithAVPrefixStillMatches()
    {
        var name = ArchiveName.TryParse("ERQuestLog v1.1.3 (DLL Version)-3585-v1-1-3-1772125971.rar", "v1.1.3");

        name!.ModId.Should().Be(3585u);
        name.Name.Should().Be("ERQuestLog v1.1.3 (DLL Version)");
    }

    [Fact]
    public void ANewStyleNameGivesTheModIdAndTheUploadMinute()
    {
        var name = ArchiveName.TryParse("QuestPath 10363 1.3.0 2026-07-19T20-46Z NhlU57Tcv.rar", "1.3.0");

        name.Should().NotBeNull();
        name!.Name.Should().Be("QuestPath");
        name.ModId.Should().Be(10363u);
        name.Version.Should().Be("1.3.0");
        name.Uploaded.Should().Be(new DateTimeOffset(2026, 7, 19, 20, 46, 0, TimeSpan.Zero));
        name.MinuteResolution.Should().BeTrue();
    }

    [Fact]
    public void ANewStyleNameWithSpacesAndDigitsInTheTitleKeepsTheRightId()
    {
        var name = ArchiveName.TryParse("Vanilla or Randomizer - v2.1.4 10062 v2.1.4 2026-09-16T15-03Z 9KB6bvSvw.zip", null);

        name!.ModId.Should().Be(10062u);
        name.Name.Should().Be("Vanilla or Randomizer - v2.1.4");
    }

    [Theory]
    [InlineData("erd-tools-1.4.0.zip")]
    [InlineData("me3-host-v0.13.0.tar.gz")]
    [InlineData("")]
    [InlineData("-1-2-3.zip")]
    [InlineData("Name-0-1-1652303340.zip")]
    [InlineData("Name-99999999999-1-1652303340.zip")]
    [InlineData("Name 12 1.0 2026-13-45T99-99Z abc.zip")]
    public void ANameThatIsNeitherStyleGivesNothing(string asset)
        => ArchiveName.TryParse(asset, "1").Should().BeNull();

    [Fact]
    public void AnOldStyleNameWhoseVersionDisagreesGivesNothing()
        => ArchiveName.TryParse("Thing-118-3-0-1652303340.zip", "9.9").Should().BeNull();

    [Fact]
    public void AnOldStyleNameWithoutAVersionIsOnlyAcceptedWhenOneReadingFits()
    {
        ArchiveName.TryParse("Thing-118-3-0-1652303340.zip", null).Should().BeNull("118 and 3 could each be the id");
        ArchiveName.TryParse("Thing-118-3-1652303340.zip", null)!.ModId.Should().Be(118u);
    }
}
