using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.App.UI.Pages.MyLoadouts.GameLoadoutsSectionEntry;
using SilverModManager.Sdk.Games;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// A game's section on the My Loadouts page lists the loadouts on that install. The filter runs inside the
/// database's event loop, where a throw wedges every later commit, so a loadout whose game can no longer be
/// found has to be left out instead.
/// </summary>
public class GameLoadoutsSectionEntryFilterTests(IServiceProvider provider) : AUiTest(provider)
{
    private IGameRegistry Registry => Provider.GetRequiredService<IGameRegistry>();

    [Fact]
    public async Task ALoadoutOnTheInstallIsListed()
    {
        var loadout = await SeedLoadoutRow(GameStore.Unknown, "section-on-install-" + Guid.NewGuid().ToString("N"));
        Registry.TryGetGameInstallation(loadout, out var installation).Should().BeTrue();

        GameLoadoutsSectionEntryViewModel.IsOnInstallation(Registry, loadout, installation!).Should().BeTrue();
    }

    [Fact]
    public async Task ALoadoutWhoseGameCannotBeFoundIsLeftOutWithoutThrowing()
    {
        var present = await SeedLoadoutRow(GameStore.Unknown, "section-present-" + Guid.NewGuid().ToString("N"));
        Registry.TryGetGameInstallation(present, out var installation).Should().BeTrue();
        var missing = await SeedLoadoutRow(GameStore.GOG, "section-missing-" + Guid.NewGuid().ToString("N"));
        Registry.TryGetGameInstallation(missing, out _).Should().BeFalse("no GOG install of the stubbed game is located");

        GameLoadoutsSectionEntryViewModel.IsOnInstallation(Registry, missing, installation!).Should().BeFalse();
    }
}
