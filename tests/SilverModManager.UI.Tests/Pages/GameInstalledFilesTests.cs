using DynamicData;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages.LoadoutGroupFilesPage;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;
using NSubstitute;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// A game-installed item's files page lists its paths, and the External Changes page finds the claim behind a
/// path so it can offer the undo.
/// </summary>
public class GameInstalledFilesTests(IServiceProvider provider) : AUiTest(provider)
{
    private async Task<(Loadout.ReadOnly Loadout, EntityId Item)> ItemWithFiles(params string[] paths)
    {
        var loadout = await SeedLoadoutRow(GameStore.Unknown, "game-installed-files-" + Guid.NewGuid().ToString("N"));
        using var tx = Connection.BeginTransaction();
        _ = new GameInstalledItem.New(tx, out var itemId)
        {
            Source = "SkyrimCreation",
            SourceId = "item",
            Confidence = "GameRecord",
            CanBeClaimed = false,
            DepotShipped = false,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, itemId)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, itemId) { Name = "Item", LoadoutId = loadout.Id },
            },
        };
        foreach (var path in paths)
        {
            _ = new GameInstalledFile.New(tx, out var fileId)
            {
                Path = new GamePath(LocationId.Game, path).ToGamePathParentTuple(loadout.Id),
                Hash = Hash.From(0xAB),
                Size = Size.From(1234),
                LoadoutItem = new LoadoutItem.New(tx, fileId) { Name = path, LoadoutId = loadout.Id, ParentId = itemId },
            };
        }

        var result = await tx.Commit();
        return (loadout, result[itemId]);
    }

    [Fact]
    public async Task TheFilesPageListsAGameInstalledItemsPaths()
    {
        var (_, item) = await ItemWithFiles("Data/a.esm", "Data/b.bsa");
        var files = new LoadoutGroupFilesProvider(Provider);

        var cache = files.ObserveModFiles(new ModFilesFilter([LoadoutItemGroupId.From(item)]), useFullFilePaths: true).AsObservableCache();

        await Eventually(() => cache.Count.Should().Be(2));
        cache.Keys.Should().BeEquivalentTo(new[] { new GamePath(LocationId.Game, "Data/a.esm"), new GamePath(LocationId.Game, "Data/b.bsa") });
        cache.Items.Select(row => row.Get<SizeComponent>(SharedColumns.ItemSizeOverGamePath.ComponentKey).Value.Value)
            .Should().OnlyContain(size => size == Size.From(1234));
    }

    [Fact]
    public async Task AClaimIsFoundByLoadoutAndPath()
    {
        var loadout = await SeedLoadoutRow(GameStore.Unknown, "claims-" + Guid.NewGuid().ToString("N"));
        var path = new GamePath(LocationId.Game, "Data/claimed.pak");
        using var tx = Connection.BeginTransaction();
        var newClaim = new GameInstalledClaim.New(tx)
        {
            LoadoutId = loadout.LoadoutId,
            Path = path.ToGamePathParentTuple(loadout.Id),
            Source = "Bg3InGameMod",
            SourceId = "claimed.pak",
            ClaimedAt = DateTimeOffset.UtcNow,
        };
        var result = await tx.Commit();

        var found = GameInstalledClaimLookup.Find(Connection.Db, loadout.LoadoutId, path);
        found.HasValue.Should().BeTrue();
        found.Value.Value.Should().Be(result.Remap(newClaim).Id);

        GameInstalledClaimLookup.Find(Connection.Db, loadout.LoadoutId, new GamePath(LocationId.Game, "Data/other.pak")).HasValue.Should().BeFalse();
    }

    private readonly IGameInstalledContentService _service = Substitute.For<IGameInstalledContentService>();
    private readonly IWindowNotificationService _notifications = Substitute.For<IWindowNotificationService>();

    /// <summary>
    /// A files page over a plain group in a fresh loadout, standing in for External Changes, with one claimed path.
    /// </summary>
    private async Task<(LoadoutGroupFilesViewModel Page, GameInstalledClaimId Claim, GamePath Claimed)> PageWithAClaim()
    {
        var loadout = await SeedLoadoutRow(GameStore.Unknown, "undo-claim-" + Guid.NewGuid().ToString("N"));
        var claimed = new GamePath(LocationId.Game, "Data/claimed.pak");
        using var tx = Connection.BeginTransaction();
        _ = new LoadoutItemGroup.New(tx, out var groupId)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, groupId) { Name = "External Changes", LoadoutId = loadout.Id },
        };
        var newClaim = new GameInstalledClaim.New(tx)
        {
            LoadoutId = loadout.LoadoutId,
            Path = claimed.ToGamePathParentTuple(loadout.Id),
            Source = "Bg3InGameMod",
            SourceId = "claimed.pak",
            ClaimedAt = DateTimeOffset.UtcNow,
        };
        var result = await tx.Commit();

        var services = new ServiceCollection()
            .AddSingleton(Connection)
            .AddSingleton(_service)
            .AddSingleton(_notifications)
            .BuildServiceProvider();
        var page = await OnUi(() => Task.FromResult(new LoadoutGroupFilesViewModel(
            NullLogger<LoadoutGroupFilesViewModel>.Instance, Substitute.For<IWindowManager>(), services, Connection)
        {
            Context = new LoadoutGroupFilesPageContext { GroupIds = [LoadoutItemGroupId.From(result[groupId])], IsReadOnly = false },
        }));
        return (page, GameInstalledClaimId.From(result.Remap(newClaim).Id), claimed);
    }

    [Fact]
    public async Task TheUndoIsOnlyRunnableForAClaimedPath()
    {
        var (page, _, claimed) = await PageWithAClaim();

        await OnUi(() => page.SelectedItem = new CompositeItemModel<GamePath>(new GamePath(LocationId.Game, "Data/mine.pak")));
        page.UndoTreatAsMineCommand.CanExecute().Should().BeFalse();

        await OnUi(() => page.SelectedItem = new CompositeItemModel<GamePath>(claimed));
        page.UndoTreatAsMineCommand.CanExecute().Should().BeTrue();

        await OnUi(() => page.SelectedItem = null);
        page.UndoTreatAsMineCommand.CanExecute().Should().BeFalse();
    }

    [Fact]
    public async Task TheUndoUnclaimsTheSelectedPathsClaim()
    {
        var (page, claim, claimed) = await PageWithAClaim();
        _service.Unclaim(Arg.Any<GameInstalledClaimId>(), Arg.Any<CancellationToken>()).Returns(new UnclaimOutcome(true, null));

        await OnUi(() =>
        {
            page.SelectedItem = new CompositeItemModel<GamePath>(claimed);
            page.UndoTreatAsMineCommand.Execute(R3.Unit.Default);
        });

        await Eventually(() => _service.Received(1).Unclaim(claim, Arg.Any<CancellationToken>()));
        _notifications.ReceivedCalls().Should().BeEmpty("the undo went through");
    }

    [Fact]
    public async Task AnUndoThatWasRefusedIsShownToTheUser()
    {
        var (page, _, claimed) = await PageWithAClaim();
        _service.Unclaim(Arg.Any<GameInstalledClaimId>(), Arg.Any<CancellationToken>())
            .Returns(new UnclaimOutcome(false, "a mod now owns that path"));

        await OnUi(() =>
        {
            page.SelectedItem = new CompositeItemModel<GamePath>(claimed);
            page.UndoTreatAsMineCommand.Execute(R3.Unit.Default);
        });

        await Eventually(() => _notifications.Received(1).ShowToast(
            string.Format(Language.GameInstalled_UndoTreatAsMine_Failed, "a mod now owns that path"),
            Arg.Any<ToastNotificationVariant>(), Arg.Any<TimeSpan?>(),
            Arg.Any<DialogButtonDefinition[]?>(), Arg.Any<Action<ButtonDefinitionId>?>()));
    }
}
