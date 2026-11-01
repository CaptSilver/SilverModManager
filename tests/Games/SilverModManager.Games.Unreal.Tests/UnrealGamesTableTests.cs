using DynamicData.Kernel;
using FluentAssertions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Games.Unreal.Tests;

/// <summary>Invariants of the rows in <see cref="UnrealGames"/> that no single game's tests would notice.</summary>
public class UnrealGamesTableTests
{
    [Fact]
    public void TheTableHoldsEveryGame()
    {
        UnrealGames.All.Should().Equal(
            UnrealGames.FinalFantasy7Remake, UnrealGames.FinalFantasy7Rebirth, UnrealGames.Stalker2,
            UnrealGames.Palworld, UnrealGames.ReadyOrNot, UnrealGames.Dispatch);
    }

    // Steam ids, names and launch executables from each app's Steam launch entry (api.steamcmd.net); project folders
    // from the Mod Organizer 2 basic_games plugins and Fluorine Manager's copies; Nexus ids from games.json by domain.
    public static TheoryData<string, string, string, string, uint, uint> Rows => new()
    {
        { "Unreal.FinalFantasy7Remake", "Final Fantasy VII Remake", "ff7remake.exe", "End", 1462040, 4202 },
        { "Unreal.FinalFantasy7Rebirth", "Final Fantasy VII Rebirth", "ff7rebirth.exe", "End", 2909400, 7237 },
        { "Unreal.Stalker2", "S.T.A.L.K.E.R. 2: Heart of Chornobyl", "Stalker2.exe", "Stalker2", 1643320, 6944 },
        { "Unreal.Palworld", "Palworld", "Palworld.exe", "Pal", 1623730, 6063 },
        { "Unreal.ReadyOrNot", "Ready or Not", "ReadyOrNot.exe", "ReadyOrNot", 1144200, 4205 },
        { "Unreal.Dispatch", "Dispatch", "Dispatch.exe", "Dispatch", 2592160, 8325 },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void TheRowsFollowTheirSources(string gameId, string displayName, string executable, string projectFolder, uint steamAppId, uint nexusId)
    {
        var row = UnrealGames.All.Single(candidate => candidate.GameId == GameId.From(gameId));

        row.DisplayName.Should().Be(displayName);
        row.Executable.ToString().Should().Be(executable);
        row.ProjectFolder.ToString().Should().Be(projectFolder);
        row.SteamAppIds.Should().Equal(steamAppId);
        row.NexusModsGameId.Should().Be(Optional<NexusModsGameId>.Create(NexusModsGameId.From(nexusId)));
    }

    [Fact]
    public void Stalker2IsAlsoOnGogUnderItsProductId()
    {
        UnrealGames.Stalker2.Stores.GOGProductIds.Should().Equal(1529799785L);
    }

    [Fact]
    public void EveryRowHasAnIdThatNamesTheFamilyAndItsOwnStoreIds()
    {
        UnrealGames.All.Select(row => row.GameId).Should().OnlyHaveUniqueItems();
        UnrealGames.All.Select(row => row.GameId.ToString()).Should().OnlyContain(id => id.StartsWith("Unreal."));
        UnrealGames.All.SelectMany(row => row.SteamAppIds).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void TheStoreIdentifiersCarryTheGameId()
    {
        foreach (var row in UnrealGames.All)
            row.Stores.GameId.Should().Be(row.GameId);
    }

    [Fact]
    public void EveryRowHasATestClassThatRunsTheSharedRowTests()
    {
        var testClasses = typeof(UnrealGamesTableTests).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false } && InheritsFromRowTests(type))
            .ToArray();

        testClasses.Should().HaveCount(UnrealGames.All.Count);
    }

    private static bool InheritsFromRowTests(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(Fixtures.AUnrealRowTests<>)) return true;
        }

        return false;
    }

    [Fact]
    public void TheLayoutOfARowIsItsProjectsContentAndBinaries()
    {
        var layout = UnrealGames.Palworld.Layout;

        layout.TryRoute(NexusMods.Paths.RelativePath.FromUnsanitizedInput("Mod_P.pak"), out var pak).Should().BeTrue();
        pak.Should().Be(new GamePath(LocationId.Game, "Pal/Content/Paks/~mods/Mod_P.pak"));
    }
}
