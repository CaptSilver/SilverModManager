using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.App.UI.Helpers;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.UI.Tests;

/// <summary>
/// The Unity games take their mods from Thunderstore and have no Nexus id, so the places that read a game's Nexus id
/// have to treat them like any other game without one.
/// </summary>
public class UnityGamesNexusIdTests(IServiceProvider provider)
{
    private static readonly NexusModsGameId SharedNexusId = NexusModsGameId.From(4242);

    private IGameData[] UnityGames => provider.GetServices<IGameData>()
        .Where(game => game.GameId == GameId.From("Unity.Repo") || game.GameId == GameId.From("Unity.Peak"))
        .ToArray();

    [Fact]
    public void BothGamesAreRegisteredWithTheApp()
    {
        UnityGames.Select(game => game.GameId).Should().BeEquivalentTo([GameId.From("Unity.Repo"), GameId.From("Unity.Peak")]);
    }

    [Fact]
    public void NeitherGameIsOnNexusMods()
    {
        UnityGames.Should().OnlyContain(game => !game.IsOnNexusMods());
    }

    [Fact]
    public void NeitherGameOwnsNexusCollections()
    {
        var db = provider.GetRequiredService<IConnection>().Db;

        foreach (var game in UnityGames) game.CollectionsOf(db).Should().BeEmpty();
    }

    [Fact]
    public void NxmRoutingNeverPicksEitherGame()
    {
        NexusModsGameScope.ResolveNxmGame(UnityGames, SharedNexusId, _ => true, NullLogger.Instance)
            .HasValue.Should().BeFalse();
    }
}
