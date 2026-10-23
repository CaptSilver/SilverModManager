using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Games.Larian.Tests.BaldursGate3;

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

    [Fact]
    public void Modsettings_IsAnIntrinsicFile()
    {
        var synchronizer = (ALoadoutSynchronizer)serviceProvider
            .GetServices<IGame>().OfType<Larian.BaldursGate3.BaldursGate3>().Single().Synchronizer;

        synchronizer.IntrinsicFiles(default).Keys.Should()
            .ContainSingle().Which.Should().Be(new GamePath(Larian.BaldursGate3.Bg3Constants.PlayerProfilesLocationId, "modsettings.lsx"));
    }
}
