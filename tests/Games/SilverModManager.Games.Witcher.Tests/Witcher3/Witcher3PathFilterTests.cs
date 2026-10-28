using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Witcher.Witcher3;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Witcher.Tests.Witcher3;

public class Witcher3PathFilterTests(IServiceProvider serviceProvider)
{
    private readonly IGamePathFilter _filter = serviceProvider
        .GetServices<IGame>().OfType<Witcher3Game>().Single().Synchronizer.GamePathFilter;

    [Fact]
    public void Documents_FiltersGameSaves()
    {
        _filter.ShouldFilter(new GamePath(WitcherConstants.WitcherDocuments, "gamesaves/AutoSave_1.sav")).Should().BeTrue();
    }

    [Fact]
    public void Documents_KeepsModsSettings()
    {
        _filter.ShouldFilter(WitcherConstants.W3ModsSettings).Should().BeFalse();
    }

    [Fact]
    public void Documents_KeepsUserSettings()
    {
        _filter.ShouldFilter(new GamePath(WitcherConstants.WitcherDocuments, "user.settings")).Should().BeFalse();
    }

    // Already true of the base filter; guards against a synchronizer that replaces it.
    [Fact]
    public void SavesLocation_IsFiltered()
    {
        _filter.ShouldFilter(new GamePath(LocationId.Saves, "x")).Should().BeTrue();
    }
}
