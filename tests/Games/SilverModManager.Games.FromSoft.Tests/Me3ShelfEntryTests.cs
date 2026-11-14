using FluentAssertions;
using SilverModManager.Games.FromSoft.ArmoredCore6;
using SilverModManager.Games.FromSoft.DarkSouls3;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.EldenRingNightreign;
using SilverModManager.Games.FromSoft.Me3;
using SilverModManager.Games.FromSoft.Sekiro;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.FromSoft.Tests;

public class Me3ShelfEntryTests
{
    [Fact]
    public void Me3SitsOnTheShelfWithTheAssetTheProvisionerTakes()
    {
        var entry = Me3HostFiles.ShelfEntry;

        entry.Owner.Should().Be("garyttierney");
        entry.Repository.Should().Be("me3");
        entry.AssetSuffix.Should().Be(Me3HostFiles.ReleaseAssetSuffix);
        entry.NameHint.Should().Be(Me3HostFiles.ReleaseAssetHint);
        entry.RepositoryId.Should().Be(Me3HostFiles.RepositoryId);
    }

    [Theory]
    [InlineData(typeof(EldenRingGame))]
    [InlineData(typeof(EldenRingNightreignGame))]
    [InlineData(typeof(DarkSouls3Game))]
    [InlineData(typeof(SekiroGame))]
    [InlineData(typeof(ArmoredCore6Game))]
    public void EveryGameMe3LaunchesIsAShelfGame(Type game)
    {
        typeof(IGitHubShelfGame).IsAssignableFrom(game).Should().BeTrue();
    }
}
