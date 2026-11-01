using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using NexusMods.Paths;

namespace SilverModManager.Games.LoadOrderFile.Tests.Fixtures;

/// <summary>
/// What every row of <see cref="LoadOrderFileGames"/> must do against its own definition: be found by its executable,
/// keep nothing outside the game folder, and write its order file on Apply from the loadout's priorities. A game's
/// test class derives from this, so a row cannot be added without these running against it.
/// </summary>
public abstract class ALoadOrderFileRowTests<TTest>(ITestOutputHelper outputHelper, LoadOrderFileGameDefinition definition)
    : ALoadOrderFileGameTests<TTest>(outputHelper, definition)
{
    /// <summary>The archive paths of a mod called <paramref name="mod"/> that the game's installer takes.</summary>
    protected abstract string[] ArchivePathsOf(string mod);

    private GamePath OrderFilePath => new(LocationId.Game, Definition.OrderFile);

    private AbsolutePath OrderFileOnDisk => GameInstallation.Locations.ToAbsolutePath(OrderFilePath);

    private async Task<LoadoutItemGroup.ReadOnly> InstallMod(Loadout.ReadOnly loadout, string mod)
    {
        var archive = await AddArchiveNamed($"{mod}.zip", ArchivePathsOf(mod));
        var groups = await LoadoutManager.InstallItems([archive.AsLibraryFile().AsLibraryItem()], loadout.LoadoutId);
        return groups.Single();
    }

    private async Task Prioritise(Loadout.ReadOnly loadout, params LoadoutItemGroup.ReadOnly[] winnerToLoser)
    {
        using var tx = Connection.BeginTransaction();
        LoadoutManager.SetGroupOrder(loadout.LoadoutId, winnerToLoser.Select(group => group.LoadoutItemGroupId).ToArray(), tx);
        await tx.Commit();
    }

    [Fact]
    public void TheLocatedFolderHoldsTheExecutableTheGameLaunches()
    {
        var primaryFile = Game.GetPrimaryFile(GameInstallation);

        primaryFile.Should().Be(new GamePath(LocationId.Game, Definition.Executable));
        GameInstallation.Locations[LocationId.Game].Path.Combine(primaryFile.Path).FileExists
            .Should().BeTrue($"{Definition.DisplayName} is found by {Definition.Executable}");
    }

    [Fact]
    public void TheGameFolderIsTheOnlyLocation()
    {
        GameInstallation.Locations.Keys.Should().BeEquivalentTo([LocationId.Game]);
    }

    [Fact]
    public async Task TheSynchronizerGeneratesTheOrderFileAtTheRowsPath()
    {
        var loadout = await CreateLoadout();

        ((ALoadoutSynchronizer)Synchronizer).IntrinsicFiles(loadout).Should().ContainKey(OrderFilePath);
    }

    [Fact]
    public async Task ApplyWritesTheModsLoserFirstAndTheWinnerLast()
    {
        var loadout = await CreateLoadout();
        var alpha = await InstallMod(loadout, "Alpha");
        var beta = await InstallMod(loadout, "Beta");
        await Prioritise(loadout, beta, alpha);

        await Synchronizer.Synchronize(Refresh(loadout));

        (await OrderFileOnDisk.ReadAllTextAsync()).Should().Be(Definition.Format.Render(["Alpha", "Beta"]));
    }

    [Fact]
    public async Task MovingAModUpTheLoadOrderMovesItInTheFileOnTheNextApply()
    {
        var loadout = await CreateLoadout();
        var alpha = await InstallMod(loadout, "Alpha");
        var beta = await InstallMod(loadout, "Beta");
        await Prioritise(loadout, beta, alpha);
        await Synchronizer.Synchronize(Refresh(loadout));

        await Prioritise(loadout, alpha, beta);
        await Synchronizer.Synchronize(Refresh(loadout));

        (await OrderFileOnDisk.ReadAllTextAsync()).Should().Be(Definition.Format.Render(["Beta", "Alpha"]));
    }

    [Fact]
    public async Task ADisabledModIsLeftOutOfTheFile()
    {
        var loadout = await CreateLoadout();
        var alpha = await InstallMod(loadout, "Alpha");
        await InstallMod(loadout, "Beta");
        await DisableItem(alpha.Id);

        await Synchronizer.Synchronize(Refresh(loadout));

        (await OrderFileOnDisk.ReadAllTextAsync()).Should().Be(Definition.Format.Render(["Beta"]));
    }

    [Fact]
    public async Task AHandEditedOrderFileIsReplacedByTheNextApply()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Alpha");
        await Synchronizer.Synchronize(Refresh(loadout));
        await OrderFileOnDisk.WriteAllTextAsync("edited by hand");

        await Synchronizer.Synchronize(Refresh(loadout));

        (await OrderFileOnDisk.ReadAllTextAsync()).Should().Be(Definition.Format.Render(["Alpha"]));
    }
}
