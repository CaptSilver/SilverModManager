using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.CreationEngine.PluginLists;
using SilverModManager.Games.CreationEngine.Tests.SortOrder;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.Morrowind.OpenMw;

/// <summary>
/// Applies mods with plugins to Morrowind with an existing openmw.cfg. Only the content= lines for files under
/// Data Files change, and they follow the app's plugin order; builtin.omwscripts, data= lines and the lines of
/// plugins the app does not deploy stay where they are.
/// </summary>
public class MorrowindOpenMwRoundTripTests(ITestOutputHelper helper) : AOpenMwTest<MorrowindOpenMwRoundTripTests>(helper)
{
    protected override int[] RootsWithConfig => [0];

    private AbsolutePath Config => Roots.Candidates[0] / "openmw.cfg";

    private async Task<string[]> ContentLines() =>
        (await Config.ReadAllTextAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("content=")).ToArray();

    private async Task Apply(Loadout.ReadOnly loadout) => await Synchronizer.Synchronize(Refresh(loadout));

    private async Task<LoadoutItemGroup.ReadOnly> Install(Loadout.ReadOnly loadout, string fileName)
    {
        var archive = await CreateTestArchive(new Dictionary<RelativePath, byte[]> { { $"Data Files/{fileName}", Plugin() } });
        var library = await RegisterLocalArchive(archive.Path);
        var installed = await LoadoutManager.InstallItem(library.AsLibraryFile().AsLibraryItem(), loadout.LoadoutId);
        return installed.LoadoutItemGroup!.Value;
    }

    [Fact]
    public async Task OnlyTheOwnedContentLinesChangeOnApply()
    {
        var loadout = await CreateLoadout();
        var deployedMod = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "Data Files/Mod.esp"));

        var group = await Install(loadout, "Mod.esp");
        await Apply(loadout);

        deployedMod.FileExists.Should().BeTrue();
        (await ContentLines()).Should().Equal("content=builtin.omwscripts", "content=Morrowind.esm", "content=Mod.esp");
        (await Config.ReadAllTextAsync()).Should().StartWith("data=\"/games/Morrowind/Data Files\"\n");

        await LoadoutManager.RemoveItems([group.LoadoutItemGroupId]);
        await Apply(loadout);

        deployedMod.FileExists.Should().BeFalse();
        (await ContentLines()).Should().Equal("content=builtin.omwscripts", "content=Morrowind.esm");
        (await Config.ReadAllTextAsync()).Should().StartWith("data=\"/games/Morrowind/Data Files\"\n");

        // A removal leaves the file rewritten outside a write, so the next add must still reach it.
        await Install(loadout, "Second.esp");
        await Apply(loadout);

        (await ContentLines()).Should().Equal("content=builtin.omwscripts", "content=Morrowind.esm", "content=Second.esp");
        (await Config.ReadAllTextAsync()).Should().StartWith("data=\"/games/Morrowind/Data Files\"\n");
    }

    [Fact]
    public async Task TheAppsPluginOrderDecidesTheOrderOfTheLines()
    {
        var loadout = await CreateLoadout();
        await Install(loadout, "Alpha.esp");
        await Install(loadout, "Beta.esp");
        await Apply(loadout);
        (await ContentLines()).Should().Equal("content=builtin.omwscripts", "content=Morrowind.esm", "content=Alpha.esp", "content=Beta.esp");

        var variety = Game.PluginVariety();
        await variety.MoveItems(variety.SortOrderOf(Refresh(loadout), Connection), [PluginSortOrderTestHelpers.Key("Beta.esp")],
            PluginSortOrderTestHelpers.Key("Alpha.esp"), TargetRelativePosition.BeforeTarget, Connection.Db);
        await Apply(loadout);

        (await ContentLines()).Should().Equal("content=builtin.omwscripts", "content=Morrowind.esm", "content=Beta.esp", "content=Alpha.esp");
    }

    [Fact]
    public async Task ASwitchedOffPluginGetsNoLineButStaysDeployed()
    {
        var loadout = await CreateLoadout();
        await Install(loadout, "Alpha.esp");
        await Install(loadout, "Beta.esp");
        await Apply(loadout);

        var variety = Game.PluginVariety();
        await variety.SetEnabled(variety.SortOrderOf(Refresh(loadout), Connection), PluginSortOrderTestHelpers.Key("Alpha.esp"), false);
        await Apply(loadout);

        (await ContentLines()).Should().Equal("content=builtin.omwscripts", "content=Morrowind.esm", "content=Beta.esp");
        GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "Data Files/Alpha.esp")).FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task ALuaScriptListIsOrderedLikeAnyOtherPlugin()
    {
        var loadout = await CreateLoadout();
        var archive = await CreateTestArchive(new Dictionary<RelativePath, byte[]>
        {
            { "Data Files/Scripts.omwscripts", "PLAYER: scripts/player.lua\n"u8.ToArray() },
            { "Data Files/Tweak.omwaddon", Plugin() },
        });
        var library = await RegisterLocalArchive(archive.Path);
        await LoadoutManager.InstallItem(library.AsLibraryFile().AsLibraryItem(), loadout.LoadoutId);

        await Apply(loadout);

        var lines = await ContentLines();
        lines.Take(2).Should().Equal("content=builtin.omwscripts", "content=Morrowind.esm");
        lines.Skip(2).Should().BeEquivalentTo("content=Scripts.omwscripts", "content=Tweak.omwaddon");
    }

    [Fact]
    public async Task UnmanagingPutsTheUsersConfigBackByteForByte()
    {
        var original = await Config.ReadAllBytesAsync();
        var loadout = await CreateLoadout();
        await Install(loadout, "Mod.esp");
        await Apply(loadout);
        (await Config.ReadAllTextAsync()).Should().Contain("content=Mod.esp");

        await LoadoutManager.UnManage(GameInstallation);

        Config.FileExists.Should().BeTrue("the user had that file before the game was managed");
        (await Config.ReadAllBytesAsync()).Should().Equal(original);
    }

    [Fact]
    public void OpenMwIsTheMechanismAndIgnoresTheMasterFlag()
    {
        Game.PluginList(GameInstallation).Should().BeOfType<OpenMwMechanism>();
        Game.MasterFlagIgnored(GameInstallation).Should().BeTrue();
    }
}

/// <summary>
/// The game has both a Morrowind.ini and an openmw.cfg: the cfg decides the order the app starts from, and the
/// first Apply writes the app's order into both.
/// </summary>
public class MorrowindWithIniAndOpenMwRoundTripTests(ITestOutputHelper helper) : AOpenMwTest<MorrowindWithIniAndOpenMwRoundTripTests>(helper)
{
    protected override int[] RootsWithConfig => [0];

    protected override string[] PluginsOnDisk => ["A.esp", "B.esp"];

    // The cfg loads B before A; the ini says the opposite and also lists a plugin that is not in Data Files.
    protected override string ConfigText => ConfigFixture + "content=B.esp\ncontent=A.esp\n";

    protected override string MorrowindIni =>
        "[General]\r\nScreen Width=1920\r\n[Game Files]\r\nGameFile0=Morrowind.esm\r\nGameFile1=A.esp\r\nGameFile2=B.esp\r\n[Weather]\r\nSunrise Time=6\r\n";

    private AbsolutePath Config => Roots.Candidates[0] / "openmw.cfg";

    private AbsolutePath Ini => GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "Morrowind.ini"));

    [Fact]
    public async Task TheSeedComesFromTheOpenMwConfigAndBothFilesAreWrittenInThatOrder()
    {
        var loadout = await CreateLoadout();

        await Synchronizer.Synchronize(Refresh(loadout));

        var content = (await Config.ReadAllTextAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("content="));
        content.Should().Equal("content=builtin.omwscripts", "content=Morrowind.esm", "content=B.esp", "content=A.esp");
        (await Ini.ReadAllTextAsync()).Should().Be(
            "[General]\r\nScreen Width=1920\r\n[Game Files]\r\nGameFile0=Morrowind.esm\r\nGameFile1=B.esp\r\nGameFile2=A.esp\r\n[Weather]\r\nSunrise Time=6\r\n");
    }
}
