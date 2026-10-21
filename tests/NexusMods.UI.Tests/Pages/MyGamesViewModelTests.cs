using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.App.UI.Controls.MiniGameWidget.Standard;
using NexusMods.App.UI.Pages.MyGames;
using NexusMods.App.UI.Settings;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Settings;
using NSubstitute;

namespace NexusMods.UI.Tests.Pages;

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
    public void Test_SettingsFileWithTheRemovedToggleStillLoads()
    {
        var json = """{"EnableAllGames":true,"EnableCollectionSharing":true}""";

        var settings = JsonSerializer.Deserialize<ExperimentalSettings>(json, Provider.GetRequiredService<JsonSerializerOptions>());

        settings.Should().NotBeNull();
        settings!.EnableCollectionSharing.Should().BeTrue();
    }

    private sealed class ProviderWith(IServiceProvider inner, ISettingsManager settingsManager) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(ISettingsManager) ? settingsManager : inner.GetService(serviceType);
    }
}
