using System.Reactive.Linq;
using System.Text.Json;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Serialization.Attributes;
using SilverModManager.App.UI.Pages.MyLoadouts;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.UI.Tests.Pages;

public class MyLoadoutsFilterTests(IServiceProvider provider) : AUiTest(provider)
{
    private MyLoadoutsViewModel CreateViewModel(IWindowManager windows, GameInstallMetadataId? installation = null) =>
        new(windows, Connection, Provider, installation);

    private readonly List<IDisposable> _activations = [];

    private async Task<MyLoadoutsViewModel> Activated(GameInstallMetadataId? installation, IWindowManager? windows = null)
    {
        var vm = await OnUi(() => Task.FromResult(CreateViewModel(windows ?? Substitute.For<IWindowManager>(), installation)));
        await OnUi(() => { _activations.Add(vm.Activator.Activate()); });
        return vm;
    }

    /// <summary>
    /// An activated page keeps watching the shared database, and its cards read services from this test's
    /// scope. Left active, it throws on the next loadout another test adds once the scope is gone, and an
    /// exception in a database observer stops that commit from ever completing.
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        IDisposable[] activations;
        lock (_activations)
        {
            activations = _activations.ToArray();
            _activations.Clear();
        }

        if (activations.Length > 0)
            await OnUi(() => { foreach (var activation in activations) activation.Dispose(); });
        await base.DisposeAsync();
    }

    [Fact]
    public void AContextSavedBeforeTheFilterExisted_ReadsBackWithoutAGame()
    {
        var options = Provider.GetRequiredService<JsonSerializerOptions>();
        var saved = """{"$type":"SilverModManager.App.UI.Pages.MyLoadoutsPageContext"}""";

        var context = JsonSerializer.Deserialize<IPageFactoryContext>(saved, options);

        context.Should().BeOfType<MyLoadoutsPageContext>().Which.InstallationId.Should().BeNull();
    }

    [Fact]
    public async Task AContextWithAGame_RoundTripsThroughTheSavedLayoutSerializer()
    {
        var loadout = await SeedLoadoutRow(GameStore.Unknown, "Round trip");
        var options = Provider.GetRequiredService<JsonSerializerOptions>();
        var context = new MyLoadoutsPageContext { InstallationId = loadout.InstallationId };

        var json = JsonSerializer.Serialize<IPageFactoryContext>(context, options);
        var back = JsonSerializer.Deserialize<IPageFactoryContext>(json, options);

        back.Should().Be(context);
    }

    [Fact]
    public async Task TheFactory_PassesTheContextsGameToThePage()
    {
        var loadout = await SeedLoadoutRow(GameStore.Unknown, "Factory");
        var factory = new MyLoadoutsPageFactory(Provider);

        var vm = factory.CreateViewModel(new MyLoadoutsPageContext { InstallationId = loadout.InstallationId });

        vm.IsFilteredToGame.Should().BeTrue();
        factory.CreateViewModel(new MyLoadoutsPageContext()).IsFilteredToGame.Should().BeFalse();
    }

    [Fact]
    public async Task Filtered_ShowsOnlyThatGamesSectionAndNamesTheGame()
    {
        var mine = await SeedLoadoutRow(GameStore.Unknown, "Filtered mine");
        await SeedLoadoutRow(GameStore.Steam, "Filtered other");

        var filtered = await Activated(mine.InstallationId);
        var unfiltered = await Activated(null);

        await EventuallyOnUi(() =>
        {
            filtered.GameSectionViewModels.Should().ContainSingle();
            unfiltered.GameSectionViewModels.Count.Should().BeGreaterThan(1, "without a filter every game has a section");
        });
        filtered.IsFilteredToGame.Should().BeTrue();
        unfiltered.IsFilteredToGame.Should().BeFalse();
        filtered.FilterHeadingText.Should().Contain(mine.Installation.Name);
    }

    [Fact]
    public async Task ShowAllGames_ReplacesThisTabWithTheUnfilteredPage()
    {
        var loadout = await SeedLoadoutRow(GameStore.Unknown, "Show all");
        var controller = Substitute.For<IWorkspaceController>();
        var window = Substitute.For<IWorkspaceWindow>();
        window.WorkspaceController.Returns(controller);
        var windows = Substitute.For<IWindowManager>();
        windows.TryGetWindow(Arg.Any<WindowId>(), out Arg.Any<IWorkspaceWindow?>())
            .Returns(call => { call[1] = window; return true; });

        var vm = await Activated(loadout.InstallationId, windows);
        var workspace = WorkspaceId.From(Guid.NewGuid());
        var panel = PanelId.From(Guid.NewGuid());
        var tab = PanelTabId.From(Guid.NewGuid());
        vm.WorkspaceId = workspace;
        vm.PanelId = panel;
        vm.TabId = tab;
        vm.WindowId = WindowId.From(Guid.NewGuid());

        await OnUi(async () => await vm.ShowAllGamesCommand.Execute());

        var call = controller.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IWorkspaceController.OpenPage));
        var args = call.GetArguments();
        args[0].Should().Be(workspace);
        var page = ((Optional<PageData>)args[1]!).Value;
        page.FactoryId.Should().Be(MyLoadoutsPageFactory.StaticId);
        page.Context.Should().Be(new MyLoadoutsPageContext());
        ((OpenPageBehavior)args[2]!).AsT0.Should().Be(new OpenPageBehavior.ReplaceTab(panel, tab));
    }
}
