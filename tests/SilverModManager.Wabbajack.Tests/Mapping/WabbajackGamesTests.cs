using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.CreationEngine;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Wabbajack.Tests.Mapping;

public class WabbajackGamesTests
{
    // Wabbajack.DTOs/Game/Game.cs, the Bethesda family.
    private static readonly string[] UpstreamNames =
    [
        "Morrowind", "Oblivion", "Fallout3", "FalloutNewVegas", "Skyrim", "Enderal", "SkyrimSpecialEdition", "Fallout4",
        "SkyrimVR", "Fallout4VR", "EnderalSpecialEdition", "Starfield", "OblivionRemastered",
    ];

    private static bool IsCreationEngineGame(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(ACreationEngineGame<>)) return true;
        }
        return false;
    }

    [Fact]
    public void EveryCreationEngineGameTheAppRegistersHasARowInBothTables()
    {
        var services = new ServiceCollection().AddCreationEngine();
        var games = services
            .Select(d => d.ImplementationType ?? d.ServiceType)
            .Where(t => t is { IsClass: true, IsAbstract: false } && IsCreationEngineGame(t))
            .Distinct()
            .ToList();
        games.Should().NotBeEmpty();

        var ids = games.Select(t => (GameId)t.GetProperty(nameof(IGameData.GameId))!.GetValue(null)!).ToList();
        var mapped = UpstreamNames.Select(WabbajackGames.GameIdFor).ToList();

        mapped.Should().NotContainNulls();
        mapped.Cast<GameId>().Should().BeEquivalentTo(ids);
        UpstreamNames.Select(WabbajackGames.NexusDomainFor).Should().NotContainNulls();
    }

    [Theory]
    [InlineData("SkyrimVR", "skyrimspecialedition")]
    [InlineData("Fallout4VR", "fallout4")]
    [InlineData("FalloutNewVegas", "newvegas")]
    [InlineData("Skyrim", "skyrim")]
    [InlineData("SkyrimSpecialEdition", "skyrimspecialedition")]
    [InlineData("EnderalSpecialEdition", "enderalspecialedition")]
    [InlineData("OblivionRemastered", "oblivionremastered")]
    public void TheNexusDomains_FollowUpstreamsRegistry(string gameType, string domain)
    {
        WabbajackGames.NexusDomainFor(gameType)!.Value.Value.Should().Be(domain);
    }

    [Fact]
    public void AGameTheAppHasNoTitleFor_HasNoRow()
    {
        WabbajackGames.GameIdFor("StardewValley").Should().BeNull();
        WabbajackGames.NexusDomainFor("StardewValley").Should().BeNull();
    }
}
