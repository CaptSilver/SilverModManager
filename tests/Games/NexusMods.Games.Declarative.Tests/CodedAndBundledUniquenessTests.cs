using FluentAssertions;
using NexusMods.Games.Declarative.Catalog;
using NexusMods.Games.Declarative.Schema;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.Declarative.Tests;

/// <summary>
/// A game's GameId and store ids may not repeat across coded games and bundled definitions: every
/// locator keys a dictionary by store id and throws on a repeat. The catalog can't check against coded
/// games while it loads, so this does it for everything that ships.
/// </summary>
public class CodedAndBundledUniquenessTests(IEnumerable<IGameData> codedGames, GameDefinitionCatalog catalog)
{
    private const uint SkyrimSpecialEditionSteamId = 489830;

    private GameIdentity[] Coded => codedGames.Select(game => new GameIdentity(game.GetType().Name, game.GameId, game.StoreIdentifiers)).ToArray();

    private GameIdentity[] Bundled => catalog.Definitions.Select(loaded => new GameIdentity(loaded.Source, loaded.Definition.GetGameId(), loaded.Definition.ToStoreIdentifiers())).ToArray();

    [Fact]
    public void CodedGamesAreRegistered() => Coded.Select(game => game.Label).Should().Contain("SkyrimSE");

    [Fact]
    public void NoGameIdOrStoreIdIsUsedTwiceAcrossCodedGamesAndBundledDefinitions()
        => GameIdentityClashes.Find([..Coded, ..Bundled]).Should().BeEmpty();

    [Fact]
    public void ABundledDefinitionReusingASkyrimSteamIdIsReported()
    {
        var definition = DefinitionSerializer.Read(Samples.Minimal("enderal-like", SkyrimSpecialEditionSteamId));
        var clashing = new GameIdentity("games/enderal-like.yaml", definition.GetGameId(), definition.ToStoreIdentifiers());

        var clashes = GameIdentityClashes.Find([..Coded, clashing]);

        clashes.Should().ContainSingle().Which.Should().Contain("Steam app id 489830").And.Contain("SkyrimSE").And.Contain("games/enderal-like.yaml");
    }

    [Fact]
    public void ADefinitionReusingACodedGameIdIsReported()
    {
        var definition = DefinitionSerializer.Read(Samples.Minimal(codedGames.First().GameId.ToString(), 1));
        var clashing = new GameIdentity("games/same-id.yaml", definition.GetGameId(), definition.ToStoreIdentifiers());

        GameIdentityClashes.Find([..Coded, clashing]).Should().ContainSingle().Which.Should().Contain("game id");
    }
}
