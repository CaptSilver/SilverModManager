using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.App.UI.Controls.MiniGameWidget.Standard;
using SilverModManager.App.UI;
using SilverModManager.App.UI.Windows;
using SilverModManager.UI.Sdk.Dialog;
using Avalonia.Platform.Storage;
using System.Reactive.Linq;
using ReactiveUI;
using SilverModManager.App.UI.Pages.MyGames;
using SilverModManager.App.UI.Settings;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Settings;
using NSubstitute;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// My Games lists every game the app has a definition for, not just a short allowlist, whatever an older
/// settings file says about enabling unsupported games.
/// </summary>
public class MyGamesViewModelTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly GameId[] OldAllowlist = [GameId.From("StardewValley"), GameId.From("RedEngine.Cyberpunk2077")];

    [Fact]
    public async Task Test_GamesOutsideTheOldAllowlistAreListed()
    {
        // A settings file written while the toggle existed and was off.
        var settings = JsonSerializer.Deserialize<ExperimentalSettings>("""{"EnableAllGames":false}""", Provider.GetRequiredService<JsonSerializerOptions>())!;
        var settingsManager = Substitute.For<ISettingsManager>();
        settingsManager.Get<ExperimentalSettings>().Returns(settings);
        // The page also reads and watches the saved tile order, which is not what this test is about.
        var real = Provider.GetRequiredService<ISettingsManager>();
        settingsManager.Get<MyGamesOrderSettings>().Returns(real.Get<MyGamesOrderSettings>());
        settingsManager.GetChanges<MyGamesOrderSettings>(Arg.Any<string?>(), Arg.Any<bool>()).Returns(real.GetChanges<MyGamesOrderSettings>(prependCurrent: true));

        // Two registered games the old allowlist hid: one is "installed", the other only listed as supported.
        var outside = Provider.GetServices<IGameData>().OfType<IGame>().Where(g => !OldAllowlist.Contains(g.GameId)).Take(2).ToArray();
        outside.Should().HaveCount(2, "the app registers games beyond the old allowlist");
        var (installed, supported) = (outside[0], outside[1]);

        var registry = Substitute.For<IGameRegistry>();
        registry.LocateGameInstallations().Returns(ImmutableArray.Create(new GameInstallation(
            new GameLocatorResult
            {
                Game = installed,
                Path = FileSystem.Shared.FromUnsanitizedFullPath("/games/outside"),
                Store = GameStore.Steam,
                StoreIdentifier = "1",
                LocatorIds = ImmutableArray<LocatorId>.Empty,
                Locator = Substitute.For<IGameLocator>(),
            },
            default
        )));

        // The view model resolves most of its services from the IServiceProvider it is handed, so that is the one to wrap.
        var services = new ProviderWith(Provider, settingsManager);
        var viewModel = await OnUi(() => Task.FromResult(ActivatorUtilities.CreateInstance<MyGamesViewModel>(services, services, registry)));
        using var _ = await OnUi(() => Task.FromResult(viewModel.Activator.Activate()));

        await EventuallyOnUi(() =>
        {
            viewModel.InstalledGames.Select(static w => w.Installation!.Game.GameId).Should().Contain(installed.GameId);

            var listed = viewModel.SupportedGames.OfType<IMiniGameWidgetViewModel>().Select(static w => w.Game!.GameId).ToArray();
            listed.Should().Contain(supported.GameId);
        });
    }

    [Fact]
    public async Task Test_AGameAddedByFolderIsListedWithoutReactivating()
    {
        var game = Provider.GetServices<IGameData>().OfType<IGame>().First();
        var folder = FileSystem.Shared.FromUnsanitizedFullPath("/games/added-by-folder");

        var registry = Substitute.For<IGameRegistry>();
        registry.LocateGameInstallations().Returns(ImmutableArray<GameInstallation>.Empty);
        registry.Problems.Returns(ImmutableArray<GameLocatorProblem>.Empty);

        // Adding a game makes the registry report it from then on.
        var addService = Substitute.For<IAddGameByPath>();
        addService.Add(folder, null, null, null).Returns(_ =>
        {
            registry.LocateGameInstallations().Returns(ImmutableArray.Create(new GameInstallation(
                new GameLocatorResult
                {
                    Game = game,
                    Path = folder,
                    Store = GameStore.ManuallyAdded,
                    StoreIdentifier = "1",
                    LocatorIds = ImmutableArray<LocatorId>.Empty,
                    Locator = Substitute.For<IGameLocator>(),
                },
                GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty)
            )));
            return new AddGameResult.Added(game);
        });

        var interop = Substitute.For<IAvaloniaInterop>();
        interop.OpenFolderPickerAsync(Arg.Any<FolderPickerOpenOptions>()).Returns(Task.FromResult(new[] { folder }));

        var windowManager = Substitute.For<IWindowManager>();
        var services = new ProviderWith(Provider, null, (typeof(IAddGameByPath), addService), (typeof(IAvaloniaInterop), interop));
        var viewModel = await OnUi(() => Task.FromResult(ActivatorUtilities.CreateInstance<MyGamesViewModel>(services, services, registry, windowManager)));
        using var _ = await OnUi(() => Task.FromResult(viewModel.Activator.Activate()));
        await EventuallyOnUi(() => viewModel.InstalledGames.Should().BeEmpty());

        await OnUi(async () => await viewModel.AddGameFolderCommand.Execute());

        await EventuallyOnUi(() => viewModel.InstalledGames.Select(static w => w.Installation!.Game.GameId).Should().Equal(game.GameId));
    }

    [Fact]
    public async Task Test_LocatorProblemsAreShownAsANotice()
    {
        var registry = Substitute.For<IGameRegistry>();
        registry.LocateGameInstallations().Returns(ImmutableArray<GameInstallation>.Empty);
        registry.Problems.Returns(ImmutableArray.Create(
            new GameLocatorProblem("HeroicEpicLocator", "Epic game abc is installed under both Heroic config roots"),
            new GameLocatorProblem("LutrisLocator", "Lutris game xyz is in both Lutris roots")
        ));

        var viewModel = await OnUi(() => Task.FromResult(ActivatorUtilities.CreateInstance<MyGamesViewModel>(Provider, Provider, registry)));
        using var _ = await OnUi(() => Task.FromResult(viewModel.Activator.Activate()));

        await EventuallyOnUi(() =>
        {
            viewModel.LocatorProblemsNotice.Should().Contain("Epic game abc is installed under both Heroic config roots")
                .And.Contain("Lutris game xyz is in both Lutris roots");
        });
    }

    [Fact]
    public async Task Test_NoLocatorProblemsShowNoNotice()
    {
        var registry = Substitute.For<IGameRegistry>();
        registry.LocateGameInstallations().Returns(ImmutableArray<GameInstallation>.Empty);
        registry.Problems.Returns(ImmutableArray<GameLocatorProblem>.Empty);

        var viewModel = await OnUi(() => Task.FromResult(ActivatorUtilities.CreateInstance<MyGamesViewModel>(Provider, Provider, registry)));
        using var _ = await OnUi(() => Task.FromResult(viewModel.Activator.Activate()));

        await EventuallyOnUi(() => viewModel.InstalledGames.Should().BeEmpty());
        viewModel.LocatorProblemsNotice.Should().BeNull();
    }

    [Fact]
    public void Test_SettingsFileWithTheRemovedToggleStillLoads()
    {
        var json = """{"EnableAllGames":true,"EnableCollectionSharing":true}""";

        var settings = JsonSerializer.Deserialize<ExperimentalSettings>(json, Provider.GetRequiredService<JsonSerializerOptions>());

        settings.Should().NotBeNull();
        settings!.EnableCollectionSharing.Should().BeTrue();
    }

    private sealed class ProviderWith(IServiceProvider inner, ISettingsManager? settingsManager, params (Type Type, object Instance)[] overrides) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            foreach (var (type, instance) in overrides)
            {
                if (type == serviceType) return instance;
            }
            return serviceType == typeof(ISettingsManager) && settingsManager is not null ? settingsManager : inner.GetService(serviceType);
        }
    }
}
