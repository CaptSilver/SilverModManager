using DynamicData.Kernel;
using FluentAssertions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Games.REEngine.Tests;

/// <summary>Invariants of the rows in <see cref="ReEngineGames"/> that no single game's tests would notice.</summary>
public class ReEngineGamesTableTests
{
    [Fact]
    public void TheTableHoldsEveryGame()
    {
        ReEngineGames.All.Should().Equal(
            ReEngineGames.DevilMayCry5, ReEngineGames.ResidentEvil2, ReEngineGames.ResidentEvil3,
            ReEngineGames.MonsterHunterRise, ReEngineGames.MonsterHunterWilds);
    }

    // Steam ids, names and executables from each app's Steam launch entry (api.steamcmd.net); release names from the
    // assets of REFramework's latest GitHub release; Nexus ids from games.json by domain.
    public static TheoryData<string, string, string, uint, string, uint> Rows => new()
    {
        { "ReEngine.DevilMayCry5", "Devil May Cry 5", "DevilMayCry5.exe", 601150, "DMC5.zip", 2751 },
        { "ReEngine.ResidentEvil2", "Resident Evil 2 (2019)", "re2.exe", 883710, "RE2.zip", 2702 },
        { "ReEngine.ResidentEvil3", "Resident Evil 3 (2020)", "re3.exe", 952060, "RE3.zip", 3191 },
        { "ReEngine.MonsterHunterRise", "Monster Hunter Rise", "MonsterHunterRise.exe", 1446780, "MHRISE.zip", 4095 },
        { "ReEngine.MonsterHunterWilds", "Monster Hunter Wilds", "MonsterHunterWilds.exe", 2246340, "MHWILDS.zip", 6993 },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void TheRowsFollowTheirSources(string gameId, string displayName, string executable, uint steamAppId, string release, uint nexusId)
    {
        var row = ReEngineGames.All.Single(candidate => candidate.GameId == GameId.From(gameId));

        row.DisplayName.Should().Be(displayName);
        row.Executable.ToString().Should().Be(executable);
        row.SteamAppId.Should().Be(steamAppId);
        row.ReFrameworkRelease.Should().Be(release);
        row.NexusModsGameId.Should().Be(Optional<NexusModsGameId>.Create(NexusModsGameId.From(nexusId)));
    }

    [Fact]
    public void EveryRowHasItsOwnIdStoreIdAndRelease()
    {
        ReEngineGames.All.Select(row => row.GameId).Should().OnlyHaveUniqueItems();
        ReEngineGames.All.Select(row => row.GameId.ToString()).Should().OnlyContain(id => id.StartsWith("ReEngine."));
        ReEngineGames.All.Select(row => row.SteamAppId).Should().OnlyHaveUniqueItems();
        ReEngineGames.All.Select(row => row.ReFrameworkRelease).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void TheStoreIdentifiersCarryTheGameIdAndTheSteamAppId()
    {
        foreach (var row in ReEngineGames.All)
        {
            row.Stores.SteamAppIds.Should().Equal(row.SteamAppId);
            row.Stores.GameId.Should().Be(row.GameId);
        }
    }

    [Fact]
    public void EveryRowHasATestClassThatRunsTheSharedRowTests()
    {
        var testClasses = typeof(ReEngineGamesTableTests).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false } && InheritsFromRowTests(type))
            .ToArray();

        testClasses.Should().HaveCount(ReEngineGames.All.Count);
    }

    private static bool InheritsFromRowTests(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(Fixtures.AReEngineRowTests<>)) return true;
        }

        return false;
    }
}
