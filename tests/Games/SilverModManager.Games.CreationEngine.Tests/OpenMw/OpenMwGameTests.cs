using FluentAssertions;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.Modlists;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Bethesda.Loot.Conditions;
using SilverModManager.Games.CreationEngine.OpenMw;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using Xunit;
using static SilverModManager.Games.CreationEngine.OpenMw.OpenMwConstants;

namespace SilverModManager.Games.CreationEngine.Tests.OpenMw;

public class OpenMwGameTests(ITestOutputHelper helper) : AOpenMwGameTest<OpenMwGameTests>(helper)
{
    [Fact]
    public void TheGameIdAndStoresAreFixed()
    {
        CreationEngine.OpenMw.OpenMw.GameId.ToString().Should().Be("CreationEngine.OpenMW");
        Game.StoreIdentifiers.SteamAppIds.Should().BeEmpty();
        Game.StoreIdentifiers.GOGProductIds.Should().BeEmpty();
        GameInstallation.LocatorResult.Store.Should().Be(GameStore.OpenMW);
    }

    [Fact]
    public void TheLocationsAreTheMorrowindFolderTheConfigAndDataLocal()
    {
        GameInstallation.Locations[LocationId.Game].Path.Should().Be(Install.Folder);
        GameInstallation.Locations[OpenMwConfig].Path.Should().Be(Roots.NativeConfig);
        GameInstallation.Locations[OpenMwDataLocal].Path.Should().Be(DataLocal);
        Game.DataPath.Should().Be(new GamePath(OpenMwDataLocal, ""));
    }

    [Fact]
    public async Task ModsDeployIntoDataLocalNotTheMorrowindFolder()
    {
        var loadout = await CreateLoadout();
        await InstallPlugin(loadout, "Mod.esp");

        await Apply(loadout);

        (DataLocal / "Mod.esp").FileExists.Should().BeTrue();
        (Install.DataFiles / "Mod.esp").FileExists.Should().BeFalse();
        (await ContentLines()).Should().Equal("content=builtin.omwscripts", "content=Morrowind.esm", "content=Tribunal.esm", "content=Bloodmoon.esm", "content=Mod.esp");
    }

    [Fact]
    public async Task TheMorrowindFolderIsNeverIndexed()
    {
        var loadout = await CreateLoadout();
        await InstallPlugin(loadout, "Mod.esp");
        await Apply(loadout);

        Game.Synchronizer.GamePathFilter.ShouldFilter(new GamePath(LocationId.Game, "Data Files/vanilla.txt")).Should().BeTrue();
        DiskStateEntry.FindByGame(Connection.Db, Refresh(loadout).Installation)
            .Select(entry => ((GamePath)entry.Path).LocationId)
            .Should().NotContain(LocationId.Game);
        ((ALoadoutSynchronizer)Synchronizer).IsIgnoredBackupPath(new GamePath(LocationId.Game, "Data Files/Morrowind.esm")).Should().BeTrue();
    }

    [Fact]
    public async Task AStockInstallHasNoMissingMasterAndListsTheMastersEarly()
    {
        var loadout = await CreateLoadout();
        await InstallPlugin(loadout, "Mod.esp", "Morrowind.esm", "Tribunal.esm");
        await Apply(loadout);

        var diagnostics = await Game.Diagnose(Refresh(loadout));

        diagnostics.Should().NotContain(d => d.Id.Source == "SilverModManager.Games.CreationEngine" && (d.Id.Number == 1 || d.Id.Number == 2), "the masters are in the Morrowind folder, which is a plugin directory");
        Game.EarlyLoadingPlugins(GameInstallation).Should().Equal("builtin.omwscripts", "Morrowind.esm", "Tribunal.esm", "Bloodmoon.esm");
    }

    [Fact]
    public void AModOrganizerModlistIsNotOfferedForOpenMw() =>
        ((IMo2InstanceTarget)Game).UnsupportedReason(GameInstallation).Should().Contain("Morrowind");

    [Fact]
    public void PluginsAreLookedUpInTheDataFoldersThenDataLocal() =>
        Game.PluginDirectories(GameInstallation).Should().Equal(Install.DataFiles, DataLocal);

    [Fact]
    public void ALootFileConditionOnMorrowindEsmIsTrue()
    {
        var context = new SilverModManager.Games.CreationEngine.Loot.LootConditionContext(
            Game, GameInstallation, SilverModManager.Games.Bethesda.Loot.Masterlists.LootGame.OpenMW,
            Game.PluginDirectories(GameInstallation).Select(directory => directory.GetFullPath()).ToList(), [], []);
        var evaluator = new ConditionEvaluator(context);

        evaluator.Evaluate("file(\"Morrowind.esm\")").Should().BeTrue("the master is in a data= folder, not in data-local");
        evaluator.Evaluate("file(\"Absent.esm\")").Should().BeFalse();
    }

    [Fact]
    public async Task ADeletedConfigRefusesApply()
    {
        var loadout = await CreateLoadout();
        Config.Delete();

        var act = () => Synchronizer.Synchronize(Refresh(loadout));

        (await act.Should().ThrowAsync<OpenMwConfigMissingException>())
            .WithMessage($"openmw.cfg is missing from {Roots.NativeConfig}; start the OpenMW launcher once to recreate it.");
    }

    [Fact]
    public async Task AMorrowindRowAndAnOpenMwRowBothApplyOverOneInstall()
    {
        var morrowindInstallation = GameRegistry.LocateGameInstallations().Single(i => i.Game is CreationEngine.Morrowind.Morrowind);

        var openMw = await CreateLoadout();
        await InstallPlugin(openMw, "Zed.esp");

        var morrowind = await LoadoutManager.CreateLoadout(morrowindInstallation, "morrowind");
        var archive = await CreateTestArchive(new Dictionary<RelativePath, byte[]> { { "Data Files/Mod.esp", StubbedMorrowindInstall.Plugin() } });
        var library = await RegisterLocalArchive(archive.Path);
        await LoadoutManager.InstallItem(library.AsLibraryFile().AsLibraryItem(), morrowind.LoadoutId);

        await Apply(openMw);
        await morrowindInstallation.GetGame().Synchronizer.Synchronize(Refresh(morrowind));

        (DataLocal / "Zed.esp").FileExists.Should().BeTrue();
        (Install.DataFiles / "Mod.esp").FileExists.Should().BeTrue();
        (DataLocal / "Mod.esp").FileExists.Should().BeFalse("the Morrowind row's mod is not the OpenMW row's");
        (Install.DataFiles / "Zed.esp").FileExists.Should().BeFalse();
        (await ContentLines()).Should().Contain("content=Zed.esp").And.NotContain("content=Mod.esp");
        (await (Install.Folder / "Morrowind.ini").ReadAllTextAsync()).Should().Contain("Mod.esp").And.NotContain("Zed.esp");
    }
}
