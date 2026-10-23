using FluentAssertions;
using SilverModManager.Networking.ModUpdates.Tests.Helpers;
using static SilverModManager.Networking.ModUpdates.Tests.Helpers.RecordedNexusFileLists;

namespace SilverModManager.Networking.ModUpdates.Tests;

public class VariantKeyTests
{
    private static string Key(RecordedFile[] files, ulong fileId, ulong modId)
    {
        var file = ById(files, fileId);
        return VariantKey.Of(file.FileName, file.Version, modId);
    }

    [Fact]
    public void AnIsoTimestampNeverLeaksIntoTheKey()
    {
        // Tokenising before stripping shatters 2026-07-14T10-47Z into 2026/07/14t10/47z, which then
        // read as name words and matched none of the nine variants instead of exactly one.
        var key = Key(MapForGoblins, 48311, MapForGoblinsModId);

        key.Should().Be("vanilla or randomizer");
        foreach (var fragment in new[] { "2026", "07", "14t10", "47z", "zthtifwxr" })
            key.Should().NotContain(fragment);
    }

    [Fact]
    public void TheSameVariantMatchesAcrossVersions()
    {
        Key(MapForGoblins, 48311, MapForGoblinsModId).Should().Be(Key(MapForGoblins, 48939, MapForGoblinsModId));
    }

    [Fact]
    public void SiblingVariantsStayDistinct()
    {
        Key(MapForGoblins, 48931, MapForGoblinsModId).Should().Be("graceborne");
        Key(MapForGoblins, 48931, MapForGoblinsModId).Should().NotBe(Key(MapForGoblins, 48939, MapForGoblinsModId));
    }

    [Fact]
    public void NoFallDeadVariantsStayDistinct()
    {
        // Same version number, different mod: the Longtail Cat talisman version is not the plain one.
        Key(NoFallDead, 48339, NoFallDeadModId).Should().Be("nofalldead");
        Key(NoFallDead, 48340, NoFallDeadModId).Should().Be("nofalldead longtail cat version");
    }

    [Fact]
    public void BossResurrectionLiteIsNotTheFullVersion()
    {
        Key(BossResurrection, 24924, BossResurrectionModId).Should().Be("boss resurrection");
        Key(BossResurrection, 24925, BossResurrectionModId).Should().Be("boss resurrection lite");
    }

    [Fact]
    public void ForeverBuffsVariantsStayDistinct()
    {
        Key(ForeverBuffs, 39767, ForeverBuffsModId).Should().Be("csv included to merge");
        Key(ForeverBuffs, 39807, ForeverBuffsModId).Should().Be("forever buffs n all kinds of buff included");
    }

    [Fact]
    public void LegacyAndCurrentFileNameShapesYieldTheSameKey()
    {
        // 25.0 uses Name-modid-version-epoch, 26.1 uses Name modid version timestamp token.
        Key(Clevers, 34558, CleversModId).Should().Be("moveset_modpack");
        Key(Clevers, 49639, CleversModId).Should().Be("moveset_modpack");
    }

    [Fact]
    public void ASingleDigitVersionDoesNotEatNameWords()
    {
        // The version "1" has to go as a whole token, not as a substring of every word that holds a 1.
        Key(NoFallDead, 48340, NoFallDeadModId).Split(' ').Should().NotContain("1");
        VariantKey.Of("Mod 1 Remix 12-77-1-1720450828.zip", "1", 77).Should().Be("mod remix 12");
    }

    [Fact]
    public void AKeyWithoutAModIdStillSeparatesVariantsOfOnePage()
    {
        // A page's files all carry the same id, so leaving it out of the removal set keeps the comparison intact.
        var plain = ById(NoFallDead, 48339);
        var longtail = ById(NoFallDead, 48340);

        VariantKey.Of(plain.FileName, plain.Version).Should().NotBe(VariantKey.Of(longtail.FileName, longtail.Version));
        var other = ById(MapForGoblins, 48311);
        var newer = ById(MapForGoblins, 48939);
        VariantKey.Of(other.FileName, other.Version).Should().Be(VariantKey.Of(newer.FileName, newer.Version));
    }
}
