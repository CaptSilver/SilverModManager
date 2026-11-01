using DynamicData.Kernel;
using FluentAssertions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Games.LoadOrderFile.Tests;

/// <summary>Invariants of the rows in <see cref="LoadOrderFileGames"/> that no single game's tests would notice.</summary>
public class LoadOrderFileGamesTableTests
{
    [Fact]
    public void TheTableHoldsEveryGame()
    {
        LoadOrderFileGames.All.Should().Equal(LoadOrderFileGames.KingdomComeDeliverance, LoadOrderFileGames.KingdomComeDeliverance2);
    }

    // Steam ids, GOG ids and executables from each app's Steam launch entry (api.steamcmd.net), the Mod Organizer 2
    // basic_games plugins and the Vortex extension; Nexus ids from games.json by domain.
    public static TheoryData<string, string, string, uint, uint> Rows => new()
    {
        { "LoadOrderFile.KingdomComeDeliverance", "Kingdom Come: Deliverance", "Bin/Win64/KingdomCome.exe", 379430, 2298 },
        { "LoadOrderFile.KingdomComeDeliverance2", "Kingdom Come: Deliverance II", "Bin/Win64MasterMasterSteamPGO/KingdomCome.exe", 1771300, 7286 },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void TheRowsFollowTheirSources(string gameId, string displayName, string executable, uint steamAppId, uint nexusId)
    {
        var row = LoadOrderFileGames.All.Single(candidate => candidate.GameId == GameId.From(gameId));

        row.DisplayName.Should().Be(displayName);
        row.Executable.ToString().Should().Be(executable);
        row.Stores.SteamAppIds.Should().Equal(steamAppId);
        row.NexusModsGameId.Should().Be(Optional<NexusModsGameId>.Create(NexusModsGameId.From(nexusId)));
    }

    [Fact]
    public void TheGogReleaseOfTheFirstGameIsLocatedToo()
    {
        LoadOrderFileGames.KingdomComeDeliverance.Stores.GOGProductIds.Should().Equal(1719198803L);
    }

    [Fact]
    public void EveryRowHasItsOwnId()
    {
        LoadOrderFileGames.All.Select(row => row.GameId).Should().OnlyHaveUniqueItems();
        LoadOrderFileGames.All.Select(row => row.GameId.ToString()).Should().OnlyContain(id => id.StartsWith("LoadOrderFile."));
    }

    [Fact]
    public void AStoreIdIsSharedOnlyByGamesThatTellThemselvesApart()
    {
        var claims = LoadOrderFileGames.All
            .SelectMany(row => row.Stores.SteamAppIds.Select(id => (Key: $"steam:{id}", Row: row))
                .Concat(row.Stores.GOGProductIds.Select(id => (Key: $"gog:{id}", Row: row))))
            .GroupBy(claim => claim.Key)
            .Where(group => group.Count() > 1);

        foreach (var shared in claims)
            shared.Should().OnlyContain(claim => claim.Row.IdentifyingFiles.Count > 0, because: $"{shared.Key} is claimed by more than one game");
    }

    [Fact]
    public void EveryRowKeepsItsFilesInsideTheGameFolder()
    {
        foreach (var row in LoadOrderFileGames.All)
        {
            foreach (var path in new[] { row.Executable, row.ModFolder, row.OrderFile })
                path.ToString().Should().NotStartWith("/").And.NotContain("..", because: row.GameId.ToString());
        }
    }

    [Fact]
    public void EveryRowHasATestClassThatRunsTheSharedRowTests()
    {
        var testClasses = typeof(LoadOrderFileGamesTableTests).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false } && InheritsFromRowTests(type))
            .ToArray();

        testClasses.Should().HaveCount(LoadOrderFileGames.All.Count);
    }

    private static bool InheritsFromRowTests(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(Fixtures.ALoadOrderFileRowTests<>)) return true;
        }

        return false;
    }
}
