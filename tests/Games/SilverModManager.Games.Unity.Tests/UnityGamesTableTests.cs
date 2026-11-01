using DynamicData.Kernel;
using FluentAssertions;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Unity.Tests;

/// <summary>Invariants of the rows in <see cref="UnityGames"/> that no single game's tests would notice.</summary>
public class UnityGamesTableTests
{
    [Fact]
    public void TheIdsOfTheExistingGamesAreUnchanged()
    {
        // Loadouts persist these strings.
        UnityGames.Peak.GameId.Should().Be(GameId.From("Unity.Peak"));
        UnityGames.Repo.GameId.Should().Be(GameId.From("Unity.Repo"));
    }

    [Fact]
    public void TheTableHoldsEveryGame()
    {
        UnityGames.All.Should().Equal(
            UnityGames.Repo, UnityGames.Peak, UnityGames.Valheim, UnityGames.Subnautica, UnityGames.SubnauticaBelowZero, UnityGames.HollowKnightSilksong);
    }

    // Executables from each game's Steam launch entry (api.steamcmd.net) and MO2's basic_games plugin; Nexus ids from
    // games.json by domain; loader packs from the community's package list on Thunderstore.
    public static TheoryData<string, string, string, string, uint, uint, string, string> Rows => new()
    {
        { "Unity.Valheim", "Valheim", "valheim.exe", "valheim_Data", 892970, 3667, "valheim", "denikson-BepInExPack_Valheim" },
        { "Unity.Subnautica", "Subnautica", "Subnautica.exe", "Subnautica_Data", 264710, 1155, "subnautica", "Subnautica_Modding-BepInExPack" },
        { "Unity.SubnauticaBelowZero", "Subnautica: Below Zero", "SubnauticaZero.exe", "SubnauticaZero_Data", 848450, 2706, "subnautica-below-zero", "Subnautica_Modding-BepInExPack" },
        { "Unity.HollowKnightSilksong", "Hollow Knight: Silksong", "Hollow Knight Silksong.exe", "Hollow Knight Silksong_Data", 1030300, 8136, "hollow-knight-silksong", "silksong_modding-BepInExPack_Silksong" },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void TheNewRowsFollowTheirSources(string gameId, string displayName, string executable, string dataFolder, uint steamAppId, uint nexusId, string community, string loaderPackage)
    {
        var row = UnityGames.All.Single(candidate => candidate.GameId == GameId.From(gameId));

        row.DisplayName.Should().Be(displayName);
        row.Executable.ToString().Should().Be(executable);
        row.DataFolder.Should().Be(dataFolder);
        row.SteamAppId.Should().Be(steamAppId);
        row.NexusModsGameId.Should().Be(Optional<Sdk.NexusModsApi.NexusModsGameId>.Create(Sdk.NexusModsApi.NexusModsGameId.From(nexusId)));
        row.ThunderstoreCommunity.Should().Be(community);
        row.ThunderstoreLoaderPackage.Should().Be(loaderPackage);
    }

    [Fact]
    public void EveryRowHasATestClassThatRunsTheSharedRowTests()
    {
        var testClasses = typeof(UnityGamesTableTests).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false } && InheritsFromRowTests(type))
            .ToArray();

        testClasses.Should().HaveCount(UnityGames.All.Count);
    }

    private static bool InheritsFromRowTests(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(Fixtures.AUnityRowTests<>)) return true;
        }

        return false;
    }

    [Fact]
    public void EveryDataFolderIsTheExecutableNameLessItsExtensionAndData()
    {
        // The Unity player names its data folder after the executable.
        foreach (var row in UnityGames.All)
            row.DataFolder.Should().Be(Path.GetFileNameWithoutExtension(row.Executable.ToString()) + "_Data", row.GameId.ToString());
    }

    [Fact]
    public void EveryLoaderPackAndAliasIsRecognisedByItsDownloadName()
    {
        foreach (var row in UnityGames.All)
        {
            var loader = new BepInEx.BepInExLoader(row);
            foreach (var package in new[] { row.ThunderstoreLoaderPackage }.Concat(row.ThunderstoreLoaderAliases))
                loader.IsLoaderPackName($"{package}-5.4.2305.zip").Should().BeTrue($"{row.GameId} {package}");
        }
    }

    [Fact]
    public void TheSubnauticaGamesAcceptTheRetiredPacksTheirModsStillDependOn()
    {
        UnityGames.Subnautica.ThunderstoreLoaderAliases.Should().Equal("Subnautica_Modding-BepInExPack_Subnautica", "Subnautica_Modding-BepInExPack_Subnautica_Experimental");
        UnityGames.SubnauticaBelowZero.ThunderstoreLoaderAliases.Should().Equal("Subnautica_Modding-BepInExPack_BelowZero", "BepInEx-BepInExPack");
        UnityGames.Valheim.ThunderstoreLoaderAliases.Should().Equal("BepInEx-BepInExPack_Valheim");
        UnityGames.HollowKnightSilksong.ThunderstoreLoaderAliases.Should().Equal("BepInEx-BepInExPack_Silksong");
    }

    [Fact]
    public void EveryRowHasItsOwnIdStoreIdAndExecutable()
    {
        UnityGames.All.Select(row => row.GameId).Should().OnlyHaveUniqueItems();
        UnityGames.All.Select(row => row.SteamAppId).Should().OnlyHaveUniqueItems();
        UnityGames.All.Select(row => row.ThunderstoreCommunity).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void TheStoreIdentifiersCarryTheGameIdAndTheSteamAppId()
    {
        foreach (var row in UnityGames.All)
        {
            row.Stores.SteamAppIds.Should().Equal(row.SteamAppId);
            row.Stores.GameId.Should().Be(row.GameId);
        }
    }

    [Fact]
    public void OnlyRepoNarrowsTheDateWindow()
    {
        UnityGames.Repo.DateWindow.Should().Be(TimeSpan.FromDays(14));
        UnityGames.Peak.DateWindow.Should().BeNull();
    }

    [Fact]
    public void NexusIdsAreTheOnesTheGamesHadAsTypes()
    {
        UnityGames.Repo.NexusModsGameId.Should().Be(Optional<Sdk.NexusModsApi.NexusModsGameId>.Create(Sdk.NexusModsApi.NexusModsGameId.From(7398)));
        UnityGames.Peak.NexusModsGameId.Should().Be(Optional<Sdk.NexusModsApi.NexusModsGameId>.Create(Sdk.NexusModsApi.NexusModsGameId.From(7867)));
    }
}
