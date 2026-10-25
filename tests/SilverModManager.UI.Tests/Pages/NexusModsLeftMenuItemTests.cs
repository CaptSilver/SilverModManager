using FluentAssertions;
using SilverModManager.App.UI.Helpers;
using SilverModManager.App.UI.LeftMenu.Loadout;
using SilverModManager.App.UI.Pages.NexusCatalog;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

namespace SilverModManager.UI.Tests.Pages;

public class NexusModsLeftMenuItemTests(IServiceProvider provider)
{
    private static readonly LoadoutId SomeLoadout = LoadoutId.From(EntityId.From(0xBEEF));

    [Fact]
    public void AGameOnNexusMods_GetsAnEntryThatOpensThePageOfTheLoadout()
    {
        var page = NexusModsLeftMenuItem.PageFor(new IdentifiedStubbedGame(GameId.From("OnNexus"), NexusModsGameId.From(4242)), SomeLoadout);

        page.Should().NotBeNull();
        page!.FactoryId.Should().Be(NexusModsPageFactory.StaticId);
        page.Context.Should().Be(new NexusModsPageContext { LoadoutId = SomeLoadout });
    }

    [Fact]
    public void AGameWithoutANexusId_GetsNoEntry()
    {
        NexusModsLeftMenuItem.PageFor(new NoNexusIdStubbedGame(provider), SomeLoadout).Should().BeNull();
    }
}

public class NexusIdOfInstallTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly NexusModsGameId OnNexus = NexusModsGameId.From(4242);
    private static readonly NexusModsGameId StoredElsewhere = NexusModsGameId.From(77);

    private async Task<GameInstallMetadata.ReadOnly> Row(GameId game, NexusModsGameId? stored)
    {
        using var tx = Connection.BeginTransaction();
        var row = new GameInstallMetadata.New(tx)
        {
            Path = "/games/some",
            Name = "Some game",
            Store = GameStore.Steam,
            Game = game,
        };
        if (stored.HasValue) row.GameId = stored.Value;
        var result = await tx.Commit();
        return result.Remap(row);
    }

    [Fact]
    public async Task ARowWithoutAStoredId_TakesTheIdOfItsGameClass()
    {
        var game = new IdentifiedStubbedGame(GameId.From("OnNexus"), OnNexus);
        var row = await Row(game.GameId, stored: null);

        row.NexusIdOf([game]).Should().Be(OnNexus);
        row.IsOnNexusMods([game]).Should().BeTrue();
    }

    [Fact]
    public async Task TheGameClassWinsOverTheStoredId()
    {
        var game = new IdentifiedStubbedGame(GameId.From("OnNexus"), OnNexus);
        var row = await Row(game.GameId, StoredElsewhere);

        row.NexusIdOf([game]).Should().Be(OnNexus);
    }

    [Fact]
    public async Task AGameThatIsNotKnown_FallsBackToTheStoredId()
    {
        var row = await Row(GameId.From("Gone"), StoredElsewhere);

        row.NexusIdOf([]).Should().Be(StoredElsewhere);
        row.IsOnNexusMods([]).Should().BeTrue();
    }

    [Fact]
    public async Task AGameWithoutANexusIdAndNoStoredOne_IsNotOnNexusMods()
    {
        var game = new NoNexusIdStubbedGame(Provider);
        var row = await Row(NoNexusIdStubbedGame.GameId, stored: null);

        row.IsOnNexusMods([game]).Should().BeFalse();
    }

    [Fact]
    public async Task AKnownGameWithoutANexusId_IgnoresAStoredOne()
    {
        var game = new NoNexusIdStubbedGame(Provider);
        var row = await Row(NoNexusIdStubbedGame.GameId, StoredElsewhere);

        row.IsOnNexusMods([game]).Should().BeFalse("the menu gives this game no entry, so its page must not have a game either");
    }

    [Fact]
    public async Task MenuEntryAndPageAgree()
    {
        var games = new IGameData[] { new IdentifiedStubbedGame(GameId.From("OnNexus"), OnNexus), new NoNexusIdStubbedGame(Provider) };
        foreach (var game in games)
        {
            var row = await Row(game.GameId, stored: null);

            (NexusModsLeftMenuItem.PageFor(game, LoadoutId.From(EntityId.From(1))) is not null)
                .Should().Be(row.IsOnNexusMods(games), $"{game.GameId} gets the menu entry exactly when its page has a game");
        }
    }
}
