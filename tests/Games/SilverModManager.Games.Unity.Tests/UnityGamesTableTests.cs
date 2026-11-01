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
    public void TheTableHoldsBothGames()
    {
        UnityGames.All.Should().Equal(UnityGames.Repo, UnityGames.Peak);
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
