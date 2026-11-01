using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Sdk.Games;

namespace SilverModManager.UI.Tests;

/// <summary>
/// Invariants over every game the app registers, hand-written ones and rows of the simple game table alike. A row
/// that reuses another game's id registers fine and then misbehaves: two games with one id, or a store id the
/// locators give to whichever game registered first.
/// </summary>
public class RegisteredGamesInvariantTests(IServiceProvider provider)
{
    private IGameData[] Games => provider.GetServices<IGameData>().ToArray();

    private static bool HasIdentifyingFiles(IGameData game) => game is IGameIdentifyingFiles { IdentifyingFiles.Count: > 0 };

    [Fact]
    public void GameIds_AreUniqueAcrossEveryRegisteredGame()
    {
        Games.Select(game => game.GameId.ToString()).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void StoreIds_AreClaimedByOneGame_UnlessEveryClaimantDeclaresIdentifyingFiles()
    {
        var claims = Games
            .SelectMany(game => game.StoreIdentifiers.SteamAppIds.Select(id => (Key: $"steam:{id}", Game: game))
                .Concat(game.StoreIdentifiers.GOGProductIds.Select(id => (Key: $"gog:{id}", Game: game)))
                .Concat(game.StoreIdentifiers.EGSCatalogItemId.Select(id => (Key: $"egs:{id}", Game: game)))
                .Concat(game.StoreIdentifiers.AmazonProductIds.Select(id => (Key: $"amazon:{id}", Game: game))))
            .GroupBy(claim => claim.Key)
            .Where(group => group.Select(claim => claim.Game).Distinct().Count() > 1);

        foreach (var shared in claims)
        {
            var claimants = shared.Select(claim => claim.Game).Distinct().ToArray();
            claimants.All(HasIdentifyingFiles).Should().BeTrue(
                because: $"{shared.Key} is claimed by {string.Join(", ", claimants.Select(game => game.GameId))}");
        }
    }

    [Fact]
    public void EveryLocationNameAGameDeclares_CreatesAnIdWithoutAHashClash()
    {
        // A LocationId is a 16-bit hash of its string and a clash between two strings throws when the second is
        // created, so create every named location of every game here and a clash fails this test, not startup.
        var names = Games.SelectMany(game => game.LocationNames.Keys).Distinct().ToArray();

        var act = () => names.Select(LocationId.From).ToArray();

        act.Should().NotThrow();
    }
}
