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

    [Theory]
    [InlineData("mods.settings")]
    [InlineData("input.settings")]
    [InlineData("user.settings")]
    [InlineData("dx12user.settings")]
    public void Documents_KeepsTheFourManagedFiles(string file)
    {
        _filter.ShouldFilter(new GamePath(WitcherConstants.WitcherDocuments, file)).Should().BeFalse();
    }

    [Theory]
    [InlineData("screenshots/shot.png")]
    [InlineData("user.settings.bak")]
    [InlineData("gamesaves/nested/x.sav")]
    [InlineData("scripts.log")]
    [InlineData("sub/mods.settings")]
    public void Documents_IgnoresEverythingElseTheGameWritesThere(string path)
    {
        _filter.ShouldFilter(new GamePath(WitcherConstants.WitcherDocuments, path)).Should().BeTrue();
    }

    [Fact]
    public void TheGameFolder_IsNotTouchedByTheDocumentsFilter()
    {
        _filter.ShouldFilter(new GamePath(LocationId.Game, "Mods/modA/content/scripts/a.ws")).Should().BeFalse();
    }

    [Theory]
    [InlineData("content/metadata.store")]
    [InlineData("content/metadata.store.stamp")]
    [InlineData("content/content0/x64.final.redscripts")]
    public void TheGameFolder_IgnoresWhatTheGameRewritesWhenItStarts(string path)
    {
        _filter.ShouldFilter(new GamePath(LocationId.Game, path)).Should().BeTrue();
    }

    [Theory]
    [InlineData("content/content0/cookedfinal.redscripts")]
    [InlineData("content/blob0.bundle")]
    [InlineData("Mods/modA/content/metadata.store")]
    [InlineData("dlc/dlcA/content/metadata.store")]
    public void TheGameFolder_KeepsFilesSteamListsAndThoseInsideAModFolder(string path)
    {
        _filter.ShouldFilter(new GamePath(LocationId.Game, path)).Should().BeFalse();
    }

    // Already true of the base filter; guards against a synchronizer that replaces it.
    [Fact]
    public void SavesLocation_IsFiltered()
    {
        _filter.ShouldFilter(new GamePath(LocationId.Saves, "x")).Should().BeTrue();
    }
}
