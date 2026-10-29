using FluentAssertions;
using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Abstractions.Games.Tests.LoadOrder;

public class VortexModJoinerTests
{
    private static Mod NexusMod(uint modId, string version = "1.0") => new()
    {
        Name = $"Mod {modId}",
        Version = version,
        DomainName = GameDomain.From("witcher3"),
        Source = new ModSource { Type = ModSourceType.NexusMods, ModId = ModId.From(modId), FileId = FileId.From(modId) },
    };

    [Theory]
    [InlineData("Witcher-3-HD-1234-beta-1700000000", 1234u)]
    [InlineData("Mod-123-beta-2-rc-1700000000", 123u)]
    public void Find_ReadsTheNumberTheModListHas_WhenSeveralSegmentsCouldBeTheModId(string installId, uint modId)
    {
        var joiner = new VortexModJoiner([NexusMod(modId)]);

        var (index, reason) = joiner.Find(installId);

        reason.Should().BeNull();
        index.Should().Be(0);
    }

    [Fact]
    public void Find_PicksTheCandidateWhoseVersionIsTheRestOfTheId_WhenTwoModIdsExist()
    {
        // Both 123 and 2 are in the mod list; only mod 123 has the version "beta.2.rc" (dashes for dots).
        var joiner = new VortexModJoiner([NexusMod(2, "9.9"), NexusMod(123, "beta.2.rc")]);

        joiner.Find("Mod-123-beta-2-rc-1700000000").Index.Should().Be(1);
    }
}
