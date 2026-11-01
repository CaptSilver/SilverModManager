using System.Collections.Immutable;
using DynamicData;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using R3;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// The Library page tags a row whose game is shared or unassigned and offers the actions that settle it. Rows come from
/// the real local-file provider; the actions run through the same class the page's view model calls. Each test uses
/// games of its own, so the items it writes are in no real game's slice.
/// </summary>
public class LibraryDestinationActionsTests(IServiceProvider provider) : AUiTest(provider)
{
    private LibraryDestinationActions ActionsFor(GameId game)
        => new(Provider.GetRequiredService<ILibraryService>(), Connection, game);

    private async Task<EntityId> AddLocalFile(string name, Destination destination)
    {
        using var tx = Connection.BeginTransaction();
        var bytes = System.Text.Encoding.UTF8.GetBytes(name + Guid.NewGuid());
        var file = new LibraryFile.New(tx, out var id)
        {
            FileName = name,
            Hash = bytes.xxHash3(),
            Size = Size.From((ulong)bytes.Length),
            LibraryItem = new LibraryItem.New(tx, id) { Name = name },
        };
        _ = new LocalFile.New(tx, id) { LibraryFile = file, OriginalPath = $"/x/{name}" };
        LibraryDestinations.Write(tx, id, destination);
        var result = await tx.Commit();
        return result.Remap(file).Id;
    }

    /// <summary>Links the item into a loadout without touching its destination, as an install does before the library records it.</summary>
    private async Task Link(LoadoutId loadout, EntityId libraryItem)
    {
        using var tx = Connection.BeginTransaction();
        var group = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id) { LoadoutId = loadout, Name = "installed" },
        };
        _ = new LibraryLinkedLoadoutItem.New(tx, id) { LibraryItemId = libraryItem, LoadoutItemGroup = group };
        await tx.Commit();
    }

    private async Task<CompositeItemModel<EntityId>> RowFor(LoadoutId loadout, IGameData game, EntityId item)
    {
        var filter = new LibraryFilter(loadout, game);
        var rows = new LocalFileDataProvider(Provider).ObserveLibraryItems(filter).AsObservableCache();
        await Eventually(() => rows.Lookup(item).HasValue.Should().BeTrue());
        return rows.Lookup(item).Value;
    }

    private static string TagOf(CompositeItemModel<EntityId> row)
        => row.Get<LibraryComponents.DestinationTag>(LibraryColumns.Destination.TagComponentKey).Text.Value;

    private static LibraryComponents.DestinationActions ActionsOf(CompositeItemModel<EntityId> row)
        => row.Get<LibraryComponents.DestinationActions>(LibraryColumns.Actions.DestinationActionsComponentKey);

    private static LibraryItemId[] Ids(EntityId item) => [LibraryItemId.From(item)];

    private sealed class AdapterProbe(IServiceProvider provider, LibraryFilter filter) : LibraryTreeDataGridAdapter(provider, filter)
    {
        public void Hook(CompositeItemModel<EntityId> model) => BeforeModelActivationHook(model);
    }

    /// <summary>A game with a loadout, and a sibling that shares its Nexus id.</summary>
    private async Task<(TestGame Game, TestGame Sibling, LoadoutId Loadout)> Setup()
    {
        var (game, sibling) = TestGame.NewPair();
        return (game, sibling, await TestGame.AddLoadout(Connection, game));
    }

    [Fact]
    public async Task SharedItem_ShowsTheSharedTag_NamingTheOtherGame()
    {
        var (game, sibling, loadout) = await Setup();
        var item = await AddLocalFile("shared.7z", new Destination(ImmutableHashSet.Create(game.GameId, sibling.GameId), Shared: true));

        var row = await RowFor(loadout, game, item);

        // The test games are not registered, so the tag falls back to the id; a registered game's name is GameNamesTests.
        await EventuallyOnUi(() => TagOf(row).Should().Be($"Also under {sibling.GameId}: Nexus Mods does not tell the two apart"));
    }

    [Fact]
    public async Task UnassignedItem_ShowsTheUnassignedTag()
    {
        var (game, _, loadout) = await Setup();
        var item = await AddLocalFile("unassigned.7z", Destination.Unknown);

        var row = await RowFor(loadout, game, item);

        await EventuallyOnUi(() => TagOf(row).Should().Be("No game assigned"));
    }

    [Fact]
    public async Task AnItemOfOneGame_HasNoTag()
    {
        var (game, _, loadout) = await Setup();
        var item = await AddLocalFile("mine.7z", Destination.Of(game.GameId));

        var row = await RowFor(loadout, game, item);

        TagOf(row).Should().BeEmpty();
        ActionsOf(row).IsShared.Value.Should().BeFalse();
        ActionsOf(row).IsUnassigned.Value.Should().BeFalse();
    }

    [Fact]
    public async Task UseForThisGame_AddsTheGame_AndClearsUnknown()
    {
        var (game, _, loadout) = await Setup();
        var item = await AddLocalFile("unassigned.7z", Destination.Unknown);
        var row = await RowFor(loadout, game, item);

        await ActionsFor(game.GameId).UseForThisGame(Ids(item));

        var after = LibraryItem.Load(Connection.Db, item);
        after.DestinationGames.Should().BeEquivalentTo([game.GameId]);
        after.Contains(LibraryItem.DestinationUnknown).Should().BeFalse();
        await EventuallyOnUi(() => TagOf(row).Should().BeEmpty());
    }

    [Fact]
    public async Task OnlyForThisGame_RemovesTheOthers_AndClearsShared()
    {
        var (game, sibling, loadout) = await Setup();
        var item = await AddLocalFile("shared.7z", new Destination(ImmutableHashSet.Create(game.GameId, sibling.GameId), Shared: true));
        var row = await RowFor(loadout, game, item);

        await ActionsFor(game.GameId).OnlyForThisGame(Ids(item));

        var after = LibraryItem.Load(Connection.Db, item);
        after.DestinationGames.Should().BeEquivalentTo([game.GameId]);
        after.Contains(LibraryItem.DestinationsShared).Should().BeFalse();
        after.Contains(LibraryItem.DestinationUnknown).Should().BeFalse();
        await EventuallyOnUi(() => TagOf(row).Should().BeEmpty());
    }

    [Fact]
    public async Task RemoveFromThisGame_OnTheLastGame_MarksUnknown_DoesNotDelete()
    {
        var (game, _, loadout) = await Setup();
        var item = await AddLocalFile("mine.7z", Destination.Of(game.GameId));
        var row = await RowFor(loadout, game, item);

        await ActionsFor(game.GameId).RemoveFromThisGame(Ids(item));

        var after = LibraryItem.Load(Connection.Db, item);
        after.IsValid().Should().BeTrue();
        after.DestinationGames.Should().BeEmpty();
        after.Contains(LibraryItem.DestinationUnknown).Should().BeTrue();
        LocalFile.Load(Connection.Db, item).IsValid().Should().BeTrue();
        await EventuallyOnUi(() => TagOf(row).Should().Be("No game assigned"));
    }

    [Fact]
    public async Task RemoveFromThisGame_OnOneOfTwoSharedGames_LeavesTheOtherGamesRowUntagged()
    {
        var (game, sibling, loadout) = await Setup();
        var item = await AddLocalFile("shared.7z", new Destination(ImmutableHashSet.Create(game.GameId, sibling.GameId), Shared: true));
        var row = await RowFor(loadout, game, item);
        await EventuallyOnUi(() => TagOf(row).Should().NotBeEmpty());

        await ActionsFor(sibling.GameId).RemoveFromThisGame(Ids(item));

        var after = LibraryItem.Load(Connection.Db, item);
        after.DestinationGames.Should().BeEquivalentTo([game.GameId]);
        after.Contains(LibraryItem.DestinationsShared).Should().BeFalse();
        await EventuallyOnUi(() =>
        {
            TagOf(row).Should().BeEmpty();
            ActionsOf(row).IsShared.Value.Should().BeFalse();
        });
    }

    [Fact]
    public void Describe_ASharedMarkerWithNoOtherGame_NeverRendersABlankName()
    {
        var state = new DestinationRowState(IsShared: true, IsUnassigned: false, new HashSet<GameId>(), InstalledHere: false);

        LibraryComponents.DestinationTag.Describe(state, new GameNames([])).Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveFromThisGame_IsDisabled_WhileInstalledInThisGamesLoadout()
    {
        var (game, _, loadout) = await Setup();
        var item = await AddLocalFile("installed.7z", Destination.Of(game.GameId));
        var row = await RowFor(loadout, game, item);
        await EventuallyOnUi(() => ActionsOf(row).CanRemoveFromThisGame.Value.Should().BeTrue());

        await Link(loadout, item);

        await EventuallyOnUi(() => ActionsOf(row).CanRemoveFromThisGame.Value.Should().BeFalse());
    }

    [Fact]
    public async Task RemoveFromThisGame_IsDisabled_WhileInstalledInAnotherLoadoutOfThisGame()
    {
        var (game, _, loadout) = await Setup();
        var secondLoadout = await TestGame.AddLoadout(Connection, game);
        var item = await AddLocalFile("installed-elsewhere.7z", Destination.Of(game.GameId));
        var row = await RowFor(loadout, game, item);

        await Link(secondLoadout, item);

        await EventuallyOnUi(() => ActionsOf(row).CanRemoveFromThisGame.Value.Should().BeFalse());
    }

    [Fact]
    public async Task SuggestAGame_OffersTheInferencesAnswer()
    {
        var (game, sibling, loadout) = await Setup();
        var item = await AddLocalFile("linked-but-unassigned.7z", Destination.Unknown);
        await Link(loadout, item);

        var suggestion = ActionsFor(sibling.GameId).Suggest(Ids(item));

        suggestion.Games.Should().BeEquivalentTo([game.GameId]);
        LibraryItem.Load(Connection.Db, item).Contains(LibraryItem.DestinationUnknown).Should().BeTrue("a suggestion changes nothing until it is accepted");
    }

    [Fact]
    public async Task SuggestAGame_Accepted_AssignsTheSuggestion()
    {
        var (game, _, loadout) = await Setup();
        var item = await AddLocalFile("accepted.7z", Destination.Unknown);
        await Link(loadout, item);
        var actions = ActionsFor(game.GameId);

        await actions.Accept(Ids(item), actions.Suggest(Ids(item)));

        var after = LibraryItem.Load(Connection.Db, item);
        after.DestinationGames.Should().BeEquivalentTo([game.GameId]);
        after.Contains(LibraryItem.DestinationUnknown).Should().BeFalse();
    }

    [Fact]
    public async Task SuggestAGame_WithNoEvidence_OffersNothing()
    {
        var (game, _, _) = await Setup();
        var item = await AddLocalFile("no-evidence.7z", Destination.Unknown);

        ActionsFor(game.GameId).Suggest(Ids(item)).IsUnknown.Should().BeTrue();
    }

    [Theory]
    [InlineData(DestinationActionKind.UseForThisGame)]
    [InlineData(DestinationActionKind.SuggestAGame)]
    [InlineData(DestinationActionKind.OnlyForThisGame)]
    [InlineData(DestinationActionKind.RemoveFromThisGame)]
    public async Task EachMenuEntry_ReachesThePageAsOneDestinationMessage(DestinationActionKind kind)
    {
        var (game, _, loadout) = await Setup();
        var item = await AddLocalFile("menu.7z", Destination.Of(game.GameId));
        var row = await RowFor(loadout, game, item);
        var adapter = new AdapterProbe(Provider, new LibraryFilter(loadout, game));
        var messages = new List<DestinationMessage>();
        using var subscription = adapter.MessageSubject.Subscribe(message => message.Switch(_ => { }, _ => { }, _ => { }, _ => { }, _ => { }, _ => { }, _ => { }, messages.Add));

        await OnUi(() =>
        {
            adapter.Hook(row);
            var commands = ActionsOf(row);
            var command = kind switch
            {
                DestinationActionKind.UseForThisGame => commands.CommandUseForThisGame,
                DestinationActionKind.SuggestAGame => commands.CommandSuggestAGame,
                DestinationActionKind.OnlyForThisGame => commands.CommandOnlyForThisGame,
                _ => commands.CommandRemoveFromThisGame,
            };
            command.Execute(Unit.Default);
            return Task.CompletedTask;
        });

        var message = messages.Should().ContainSingle().Subject;
        message.Kind.Should().Be(kind);
        message.Ids.Should().Equal(Ids(item));
    }

    [Fact]
    public void GameNames_UseTheRegisteredDisplayName_AndFallBackToTheId()
    {
        var (known, unknown) = TestGame.NewPair();
        var names = new GameNames([known]);

        names.NameOf(known.GameId).Should().Be(known.DisplayName);
        names.NameOf(unknown.GameId).Should().Be(unknown.GameId.ToString());
    }
}
