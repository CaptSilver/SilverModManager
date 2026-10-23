using SilverModManager.Abstractions.Games.FileHashes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using SilverModManager.Abstractions.Games;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// Anything that deletes the game's own files works from the vanilla file list, so it must refuse when that list
/// does not cover the installed build: an unknown depot contributes no files, and the whole folder would look added.
/// </summary>
public class UncoveredBuildRefusalTests(ITestOutputHelper helper) : AStubbedGameTest<UncoveredBuildRefusalTests>(helper)
{
    private AbsolutePath GameFolder => GameInstallation.Locations[LocationId.Game].Path;

    private StubbedFileHasherService Hashes => (StubbedFileHasherService)ServiceProvider.GetRequiredService<IFileHashesService>();

    private Dictionary<string, string> FolderContents()
    {
        return Directory.GetFiles(GameFolder.GetFullPath(), "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, File.ReadAllText);
    }

    private async Task<Loadout.ReadOnly> LoadoutWithAnAddedFile()
    {
        var loadout = await CreateLoadout();
        (GameFolder / "added.txt").Parent.CreateDirectory();
        await (GameFolder / "added.txt").WriteAllTextAsync("added by hand");
        return await Synchronizer.Synchronize(loadout);
    }

    private static int ExternalChanges(Loadout.ReadOnly loadout)
    {
        return LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemWithTargetPath()
            .Count(f => f.AsLoadoutItem().Parent.AsLoadoutItem().Name == "Overrides");
    }

    [Fact]
    public async Task ResetToOriginalGameStateRefusesABuildTheHashesDataDoesNotCover()
    {
        await LoadoutWithAnAddedFile();
        var before = FolderContents();

        var act = async () => await Synchronizer.ResetToOriginalGameState(GameInstallation, [LocatorId.From("a-build-no-store-knows")]);

        var exception = (await act.Should().ThrowAsync<SynchronizerPreflightException>()).Which;
        exception.Message.Should().Contain(GameInstallation.Game.DisplayName);
        exception.Message.Should().Contain("kept as External Changes");
        FolderContents().Should().BeEquivalentTo(before, "no file may be deleted or rewritten");
    }

    [Fact]
    public async Task ResetToOriginalGameStateRefusesWhenOneOfTheBuildsDepotsIsUnknown()
    {
        await LoadoutWithAnAddedFile();
        var before = FolderContents();

        var act = async () => await Synchronizer.ResetToOriginalGameState(GameInstallation, [LocatorId.From("StubbedGameState.zip"), LocatorId.From("an-unknown-depot")]);

        await act.Should().ThrowAsync<SynchronizerPreflightException>();
        FolderContents().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task UnManageWithCleanRefusesABuildTheHashesDataDoesNotCover()
    {
        var loadout = await LoadoutWithAnAddedFile();
        var before = FolderContents();
        Hashes.CoversEveryBuild = false;

        var act = async () => await LoadoutManager.UnManage(GameInstallation);

        await act.Should().ThrowAsync<SynchronizerPreflightException>();
        Loadout.All(Connection.Db).Should().ContainSingle("the loadout is only removed once the game folder was cleaned");
        FolderContents().Should().BeEquivalentTo(before);
        ExternalChanges(Refresh(loadout)).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task DeactivateCurrentLoadoutRefusesABuildTheHashesDataDoesNotCoverBeforeSynchronizing()
    {
        var loadout = await LoadoutWithAnAddedFile();
        var before = FolderContents();
        Hashes.CoversEveryBuild = false;

        var act = async () => await LoadoutManager.DeactivateCurrentLoadout(GameInstallation);

        await act.Should().ThrowAsync<SynchronizerPreflightException>();
        FolderContents().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task DeletingTheActiveLoadoutRefusesABuildTheHashesDataDoesNotCoverAndKeepsItVisible()
    {
        var loadout = await LoadoutWithAnAddedFile();
        var before = FolderContents();
        Hashes.CoversEveryBuild = false;

        var act = async () => await LoadoutManager.DeleteLoadout(loadout.LoadoutId);

        await act.Should().ThrowAsync<SynchronizerPreflightException>();
        Refresh(loadout).IsVisible().Should().BeTrue("a refusal after the loadout was hidden would leave it hidden but not deleted");
        FolderContents().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task SwitchingLoadoutsRefusesABuildTheHashesDataDoesNotCover()
    {
        var first = await LoadoutWithAnAddedFile();
        var second = await LoadoutManager.CreateLoadout(GameInstallation);
        var before = FolderContents();
        Hashes.CoversEveryBuild = false;

        var act = async () => await Synchronizer.Synchronize(Refresh(second));

        await act.Should().ThrowAsync<SynchronizerPreflightException>("switching resets the folder to the vanilla file list first");
        FolderContents().Should().BeEquivalentTo(before);
        LoadoutManager.GetCurrentlyActiveLoadout(GameInstallation).Value.Should().Be(first.LoadoutId);
    }

    [Fact]
    public async Task CleanGameFolderRefusesABuildTheHashesDataDoesNotCover()
    {
        var loadout = await LoadoutWithAnAddedFile();
        var before = FolderContents();
        var rows = ExternalChanges(loadout);
        rows.Should().BeGreaterThan(0, "the test needs something to clean");
        Hashes.CoversEveryBuild = false;

        var act = async () => await LoadoutManager.CleanGameFolder(Refresh(loadout));

        await act.Should().ThrowAsync<SynchronizerPreflightException>();
        ExternalChanges(Refresh(loadout)).Should().Be(rows, "the rows are only deleted once the build is known to be covered");
        FolderContents().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task CleanGameFolderRefusesWhenOneOfTheBuildsDepotsIsUnknown()
    {
        var loadout = await LoadoutWithAnAddedFile();
        var before = FolderContents();
        var rows = ExternalChanges(loadout);
        UseLocatorIds("StubbedGameState.zip", "an-unknown-depot");

        var act = async () => await LoadoutManager.CleanGameFolder(Refresh(loadout));

        await act.Should().ThrowAsync<SynchronizerPreflightException>();
        ExternalChanges(Refresh(loadout)).Should().Be(rows);
        FolderContents().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task CleanGameFolderOnACoveredBuildStillRemovesTheAddedFiles()
    {
        var loadout = await LoadoutWithAnAddedFile();
        ExternalChanges(loadout).Should().BeGreaterThan(0);

        await LoadoutManager.CleanGameFolder(Refresh(loadout));

        ExternalChanges(Refresh(loadout)).Should().Be(0);
        File.Exists((GameFolder / "added.txt").GetFullPath()).Should().BeFalse();
    }

    private void UseLocatorIds(params string[] ids)
    {
        var locator = ServiceProvider.GetServices<IGameLocator>().OfType<UniversalStubbedGameLocator<StubbedGame>>().Single();
        locator.LocatorIds = ids.Select(LocatorId.From).ToArray();
        GameRegistry.ClearCache();
        GameInstallation = GameRegistry.LocateGameInstallations().Single(i => i.Game is StubbedGame);
    }
}
