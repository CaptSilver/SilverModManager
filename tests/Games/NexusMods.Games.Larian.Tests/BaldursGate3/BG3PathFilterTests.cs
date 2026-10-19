using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Sdk.Games;
using Xunit;

namespace NexusMods.Games.Larian.Tests.BaldursGate3;

public class BG3PathFilterTests(IServiceProvider serviceProvider)
{
    private readonly IGamePathFilter _filter = serviceProvider
        .GetServices<IGame>().OfType<Larian.BaldursGate3.BaldursGate3>().Single().Synchronizer.GamePathFilter;

    [Fact]
    public void PlayerProfiles_FiltersSaveGames()
    {
        _filter.ShouldFilter(new GamePath(Larian.BaldursGate3.Bg3Constants.PlayerProfilesLocationId, "Savegames/Story/a.lsv")).Should().BeTrue();
    }

    [Fact]
    public void PlayerProfiles_KeepsModsettings()
    {
        _filter.ShouldFilter(new GamePath(Larian.BaldursGate3.Bg3Constants.PlayerProfilesLocationId, "modsettings.lsx")).Should().BeFalse();
    }

    [Fact]
    public void SavesLocation_IsFiltered()
    {
        _filter.ShouldFilter(new GamePath(LocationId.Saves, "x")).Should().BeTrue();
    }
}
