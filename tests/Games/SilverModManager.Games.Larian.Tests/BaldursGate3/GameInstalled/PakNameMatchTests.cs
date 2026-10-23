using FluentAssertions;
using SilverModManager.Games.Larian.BaldursGate3.GameInstalled;

namespace SilverModManager.Games.Larian.Tests.BaldursGate3.GameInstalled;

public class PakNameMatchTests
{
    [Theory]
    [InlineData("mymod-ab12", "MyMod")]
    [InlineData("mymod-ab12", "mymod")]
    // The game cuts a long folder short when it names the pak, and the cut can land after a '-'.
    [InlineData("aza_shart_halfplate_edec7b9d-0-eofu", "AZA_SHART_HalfPlate_edec7b9d-0-eofu_extra_long_suffix")]
    [InlineData("aza_shart-ab12", "Aza_Shart_HalfPlate")]
    public void MatchesFolder_AcceptsEqualOrTruncatedFolder(string stem, string folder)
        => PakNameMatch.MatchesFolder(stem, folder).Should().BeTrue();

    [Theory]
    [InlineData("other-ab12", "MyMod")]
    [InlineData("mymodextra-ab12", "MyMod")] // head longer than the folder
    [InlineData("-ab12", "MyMod")] // empty head
    [InlineData("mymod", "MyMod")] // no tail
    [InlineData("mymod-abc", "MyMod")] // tail too short
    [InlineData("mymod-abcde", "MyMod")] // tail too long
    [InlineData("cool_mod-beta", "SomethingElse")] // looks like a tail but names another folder
    [InlineData("mymod-AB12", "MyMod")] // tail not lowercase
    [InlineData("MyMod-ab12", "MyMod")] // head not lowercase
    public void MatchesFolder_RejectsEverythingElse(string stem, string folder)
        => PakNameMatch.MatchesFolder(stem, folder).Should().BeFalse();

    [Theory]
    [InlineData("mymod-ab12")]
    [InlineData("a-ab12")]
    [InlineData("my.mod_v2-x9z0")]
    [InlineData("aza_shart_halfplate_edec7b9d-0-eofu")]
    public void LooksInGame_AcceptsTheGamesNaming(string stem)
        => PakNameMatch.LooksInGame(stem).Should().BeTrue();

    [Theory]
    [InlineData("cool_mod-betas")]
    [InlineData("MyMod-ab12")]
    [InlineData("mymod-AB12")]
    [InlineData("mymod")]
    [InlineData("-ab12")]
    [InlineData("my mod-ab12")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-ab12")] // 31 characters before the tail
    public void LooksInGame_RejectsOtherNames(string stem)
        => PakNameMatch.LooksInGame(stem).Should().BeFalse();

    [Fact]
    public void LooksInGame_AllowsThirtyCharactersBeforeTheTail()
        => PakNameMatch.LooksInGame(new string('a', 25) + "-ab12").Should().BeTrue();
}
