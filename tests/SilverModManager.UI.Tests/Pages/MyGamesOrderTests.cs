using System.Collections.Immutable;
using System.Collections.Specialized;
using System.Reactive.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.App.UI.Pages.MyGames;
using SilverModManager.App.UI.Settings;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>The order of the tiles on My Games: saved when a tile is moved, restored on the next visit, new games last.</summary>
public class MyGamesOrderTests(IServiceProvider provider) : AUiTest(provider)
{
    private IGame[] _games = [];

    private GameInstallation[] Installations(int count)
    {
        _games = Provider.GetServices<IGameData>().OfType<IGame>().Take(count).ToArray();
        _games.Should().HaveCount(count);
        return _games.Select((game, i) => new GameInstallation(
            new GameLocatorResult
            {
                Game = game,
                Path = FileSystem.Shared.FromUnsanitizedFullPath($"/games/order-{i}"),
                Store = GameStore.Steam,
                StoreIdentifier = i.ToString(),
                LocatorIds = ImmutableArray<LocatorId>.Empty,
                Locator = Substitute.For<IGameLocator>(),
            },
            default
        )).ToArray();
    }

    private async Task<(MyGamesViewModel ViewModel, IDisposable Activation)> Create(params GameInstallation[] installations)
    {
        var registry = Substitute.For<IGameRegistry>();
        registry.LocateGameInstallations().Returns(installations.ToImmutableArray());
        registry.Problems.Returns(ImmutableArray<GameLocatorProblem>.Empty);

        var viewModel = await OnUi(() => Task.FromResult(ActivatorUtilities.CreateInstance<MyGamesViewModel>(Provider, Provider, registry)));
        var activation = await OnUi(() => Task.FromResult(viewModel.Activator.Activate()));
        await EventuallyOnUi(() => viewModel.InstalledGames.Should().HaveCount(installations.Length));
        return (viewModel, activation);
    }

    private void SaveOrder(params GameInstallation[] order) =>
        Provider.GetRequiredService<ISettingsManager>().Update<MyGamesOrderSettings>(_ =>
            new MyGamesOrderSettings { GameKeys = order.Select(MyGamesOrder.KeyOf).ToImmutableList() });

    private static string[] Shown(MyGamesViewModel viewModel) =>
        viewModel.InstalledGames.Select(widget => widget.Installation!.LocatorResult.StoreIdentifier).ToArray();

    [Fact]
    public async Task SavedOrderDecidesPositionAndNewGamesComeLast()
    {
        var all = Installations(3);
        // 2 before 0; 1 is not listed so it follows them
        SaveOrder(all[2], all[0]);

        var (viewModel, activation) = await Create(all);
        using (activation)
        {
            await EventuallyOnUi(() => Shown(viewModel).Should().Equal("2", "0", "1"));
        }
    }

    [Fact]
    public async Task MovingATileSavesTheOrderAndSurvivesANewViewModel()
    {
        var all = Installations(3);
        SaveOrder(all);

        var (viewModel, activation) = await Create(all);
        using (activation)
        {
            await OnUi(() => viewModel.MoveInstalledGame(0, 2));
            await EventuallyOnUi(() => Shown(viewModel).Should().Equal("1", "2", "0"));
        }

        var (rebuilt, rebuiltActivation) = await Create(all);
        using (rebuiltActivation)
        {
            await EventuallyOnUi(() => Shown(rebuilt).Should().Equal("1", "2", "0"));
        }
    }

    [Fact]
    public async Task MovingATileMovesItsWidgetInsteadOfRebuildingTheList()
    {
        // A Reset makes the view throw every tile away and recreate it, which shows as the grid blinking after a drop.
        var all = Installations(3);
        SaveOrder(all);

        var (viewModel, activation) = await Create(all);
        using (activation)
        {
            var before = viewModel.InstalledGames.ToArray();
            var actions = new List<NotifyCollectionChangedAction>();
            NotifyCollectionChangedEventHandler record = (_, e) => actions.Add(e.Action);
            await OnUi(() => ((INotifyCollectionChanged)viewModel.InstalledGames).CollectionChanged += record);

            await OnUi(() => viewModel.MoveInstalledGame(0, 2));
            await EventuallyOnUi(() => Shown(viewModel).Should().Equal("1", "2", "0"));
            await OnUi(() => ((INotifyCollectionChanged)viewModel.InstalledGames).CollectionChanged -= record);

            actions.Should().NotBeEmpty().And.NotContain(NotifyCollectionChangedAction.Reset);
            viewModel.InstalledGames.Should().OnlyContain(widget => before.Contains(widget), "the same widgets are reordered, none rebuilt");
        }
    }

    [Fact]
    public async Task TheContextMenuCommandsMoveATileOneSlot()
    {
        var all = Installations(3);
        SaveOrder(all);

        var (viewModel, activation) = await Create(all);
        using (activation)
        {
            var middle = viewModel.InstalledGames[1];
            await OnUi(async () => await middle.MoveEarlier!.Execute());
            await EventuallyOnUi(() => Shown(viewModel).Should().Equal("1", "0", "2"));

            await OnUi(async () => await middle.MoveLater!.Execute());
            await EventuallyOnUi(() => Shown(viewModel).Should().Equal("0", "1", "2"));
        }
    }

    [Fact]
    public async Task TheFirstTileCannotMoveEarlierNorTheLastLater()
    {
        var all = Installations(2);
        SaveOrder(all);

        var (viewModel, activation) = await Create(all);
        using (activation)
        {
            await OnUi(async () => await viewModel.InstalledGames[0].MoveEarlier!.Execute());
            await OnUi(async () => await viewModel.InstalledGames[1].MoveLater!.Execute());
            Shown(viewModel).Should().Equal("0", "1");
        }
    }

    [Fact]
    public async Task ReactivatingThePageDoesNotListTilesTwice()
    {
        var all = Installations(2);
        var (viewModel, activation) = await Create(all);
        activation.Dispose();

        using var again = await OnUi(() => Task.FromResult(viewModel.Activator.Activate()));
        await EventuallyOnUi(() => viewModel.InstalledGames.Should().HaveCount(2));
    }

    [Fact]
    public void TheKeyTellsInstallationsOfOneGameApart()
    {
        var all = Installations(1);
        var other = new GameInstallation(
            new GameLocatorResult
            {
                Game = all[0].Game,
                Path = FileSystem.Shared.FromUnsanitizedFullPath("/games/second-copy"),
                Store = all[0].LocatorResult.Store,
                StoreIdentifier = all[0].LocatorResult.StoreIdentifier,
                LocatorIds = ImmutableArray<LocatorId>.Empty,
                Locator = Substitute.For<IGameLocator>(),
            },
            default
        );

        MyGamesOrder.KeyOf(all[0]).Should().NotBe(MyGamesOrder.KeyOf(other));
        MyGamesOrder.KeyOf(all[0]).Should().Be(MyGamesOrder.KeyOf(all[0]));
    }
}
