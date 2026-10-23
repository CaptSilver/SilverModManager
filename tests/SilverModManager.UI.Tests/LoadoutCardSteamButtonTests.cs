using System.Reactive.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.App.UI.Controls.LoadoutCard;
using NexusMods.App.UI.Overlays;
using NexusMods.App.UI.Pages.SteamLaunchOption;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NSubstitute;
using ReactiveUI;
using Loadout = NexusMods.Sdk.Loadouts.Loadout;

namespace NexusMods.UI.Tests;

public class LoadoutCardSteamButtonTests(IServiceProvider provider) : AUiTest(provider)
{
    private Task<Loadout.ReadOnly> SeedLoadout(GameStore store) => SeedLoadoutRow(store, $"Steam Button {store.Value}");

    private LoadoutCardViewModel CardFor(Loadout.ReadOnly loadout) => new(loadout, Connection, Provider)
    {
        VisitLoadoutCommand = ReactiveCommand.Create(() => { }),
    };

    [Fact]
    public async Task Test_SteamInstallation_CanUseFromSteam()
    {
        var card = CardFor(await SeedLoadout(GameStore.Steam));

        card.CanUseFromSteam.Should().BeTrue();
    }

    [Fact]
    public async Task Test_GogInstallation_CannotUseFromSteam()
    {
        var card = CardFor(await SeedLoadout(GameStore.GOG));

        card.CanUseFromSteam.Should().BeFalse();
    }

    [Fact]
    public async Task Test_TheDialogUsesTheLoadoutsCurrentName()
    {
        var seeded = await SeedLoadout(GameStore.Steam);
        var overlays = Substitute.For<IOverlayController>();
        var card = new LoadoutCardViewModel(seeded, Connection, new OverlayProvider(Provider, overlays))
        {
            VisitLoadoutCommand = ReactiveCommand.Create(() => { }),
        };

        // The card was built from the snapshot taken before the rename.
        await Provider.GetRequiredService<ILoadoutManager>().RenameLoadout(seeded.LoadoutId, "Renamed Steam Button");
        await card.UseFromSteamCommand.Execute();

        var dialog = overlays.ReceivedCalls().SelectMany(call => call.GetArguments()).OfType<SteamLaunchOptionViewModel>().Single();
        dialog.LoadoutName.Should().Be("Renamed Steam Button");
    }

    /// <summary>
    /// The real services, except that overlays go to a recorder instead of the (absent) window.
    /// </summary>
    private sealed class OverlayProvider(IServiceProvider inner, IOverlayController overlays) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IOverlayController) ? overlays : inner.GetService(serviceType);
    }
}
