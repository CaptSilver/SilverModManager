using FluentAssertions;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.CreationEngine.PluginLists;
using SilverModManager.Games.CreationEngine.Tests.SortOrder;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.OpenMw;

/// <summary>
/// Applies mods with plugins to OpenMW with an existing openmw.cfg. Only the content= lines for the plugins the app
/// deploys change, and they follow the app's plugin order; builtin.omwscripts, the masters, data= lines and the lines
/// of plugins the app does not deploy stay where they are.
/// </summary>
public class OpenMwRoundTripTests(ITestOutputHelper helper) : AOpenMwGameTest<OpenMwRoundTripTests>(helper)
{
    private static readonly string[] Stock = ["content=builtin.omwscripts", "content=Morrowind.esm", "content=Tribunal.esm", "content=Bloodmoon.esm"];

    private string[] StockWith(params string[] mods) => [.. Stock, .. mods];

    private string DataLine => $"data=\"{Install.DataFiles.GetFullPath()}\"\n";

    [Fact]
    public async Task OnlyTheOwnedContentLinesChangeOnApply()
    {
        var loadout = await CreateLoadout();
        var deployedMod = DataLocal / "Mod.esp";

        var group = await InstallPlugin(loadout, "Mod.esp");
        await Apply(loadout);

        deployedMod.FileExists.Should().BeTrue();
        (await ContentLines()).Should().Equal(StockWith("content=Mod.esp"));
        (await Config.ReadAllTextAsync()).Should().StartWith(DataLine);

        await LoadoutManager.RemoveItems([group.LoadoutItemGroupId]);
        await Apply(loadout);

        deployedMod.FileExists.Should().BeFalse();
        (await ContentLines()).Should().Equal(Stock);
        (await Config.ReadAllTextAsync()).Should().StartWith(DataLine);

        // A removal leaves the file rewritten outside a write, so the next add must still reach it.
        await InstallPlugin(loadout, "Second.esp");
        await Apply(loadout);

        (await ContentLines()).Should().Equal(StockWith("content=Second.esp"));
        (await Config.ReadAllTextAsync()).Should().StartWith(DataLine);
    }

    private async Task<LoadoutItemGroup.ReadOnly> InstallArchive(Loadout.ReadOnly loadout, string fileName)
    {
        var archive = await CreateTestArchive(new Dictionary<RelativePath, byte[]> { { $"Data Files/{fileName}", "bsa"u8.ToArray() } });
        var library = await RegisterLocalArchive(archive.Path);
        return (await LoadoutManager.InstallItem(library.AsLibraryFile().AsLibraryItem(), loadout.LoadoutId)).LoadoutItemGroup!.Value;
    }

    private async Task<string[]> ArchiveLines() =>
        (await Config.ReadAllTextAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("fallback-archive=")).ToArray();

    [Fact]
    public async Task ADeployedBsaGetsAFallbackArchiveLine()
    {
        var loadout = await CreateLoadout();
        await InstallArchive(loadout, "Meshes.bsa");
        await Apply(loadout);

        (DataLocal / "Meshes.bsa").FileExists.Should().BeTrue();
        (await ArchiveLines()).Should().Equal("fallback-archive=Meshes.bsa");
        (await ContentLines()).Should().Equal(Stock);

        // A second Apply with nothing changed must not double the line.
        await Apply(loadout);
        (await ArchiveLines()).Should().Equal("fallback-archive=Meshes.bsa");
    }

    [Fact]
    public async Task AnUndeployedBsaLosesItsLine()
    {
        var loadout = await CreateLoadout();
        var group = await InstallArchive(loadout, "Meshes.bsa");
        await Apply(loadout);
        (await ArchiveLines()).Should().Equal("fallback-archive=Meshes.bsa");

        await LoadoutManager.RemoveItems([group.LoadoutItemGroupId]);
        await Apply(loadout);

        (DataLocal / "Meshes.bsa").FileExists.Should().BeFalse();
        (await ArchiveLines()).Should().BeEmpty();
    }

    [Fact]
    public async Task TheUsersOwnFallbackArchiveLinesStay()
    {
        Roots.WriteConfig(0, "fallback-archive=Morrowind.bsa\n" + ConfigText);
        var loadout = await CreateLoadout();
        await InstallArchive(loadout, "Meshes.bsa");
        await Apply(loadout);

        (await ArchiveLines()).Should().Equal("fallback-archive=Morrowind.bsa", "fallback-archive=Meshes.bsa");
    }

    [Fact]
    public async Task TheAppsPluginOrderDecidesTheOrderOfTheLines()
    {
        var loadout = await CreateLoadout();
        await InstallPlugin(loadout, "Alpha.esp");
        await InstallPlugin(loadout, "Beta.esp");
        await Apply(loadout);
        (await ContentLines()).Should().Equal(StockWith("content=Alpha.esp", "content=Beta.esp"));

        var variety = Game.PluginVariety();
        await variety.MoveItems(variety.SortOrderOf(Refresh(loadout), Connection), [PluginSortOrderTestHelpers.Key("Beta.esp")],
            PluginSortOrderTestHelpers.Key("Alpha.esp"), TargetRelativePosition.BeforeTarget, Connection.Db);
        await Apply(loadout);

        (await ContentLines()).Should().Equal(StockWith("content=Beta.esp", "content=Alpha.esp"));
    }

    [Fact]
    public async Task ASwitchedOffPluginGetsNoLineButStaysDeployed()
    {
        var loadout = await CreateLoadout();
        await InstallPlugin(loadout, "Alpha.esp");
        await InstallPlugin(loadout, "Beta.esp");
        await Apply(loadout);

        var variety = Game.PluginVariety();
        await variety.SetEnabled(variety.SortOrderOf(Refresh(loadout), Connection), PluginSortOrderTestHelpers.Key("Alpha.esp"), false);
        await Apply(loadout);

        (await ContentLines()).Should().Equal(StockWith("content=Beta.esp"));
        (DataLocal / "Alpha.esp").FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task ALuaScriptListIsOrderedLikeAnyOtherPlugin()
    {
        var loadout = await CreateLoadout();
        var archive = await CreateTestArchive(new Dictionary<RelativePath, byte[]>
        {
            { "Data Files/Scripts.omwscripts", "PLAYER: scripts/player.lua\n"u8.ToArray() },
            { "Data Files/Tweak.omwaddon", StubbedMorrowindInstall.Plugin() },
        });
        var library = await RegisterLocalArchive(archive.Path);
        await LoadoutManager.InstallItem(library.AsLibraryFile().AsLibraryItem(), loadout.LoadoutId);

        await Apply(loadout);

        var lines = await ContentLines();
        lines.Take(4).Should().Equal(Stock);
        lines.Skip(4).Should().BeEquivalentTo("content=Scripts.omwscripts", "content=Tweak.omwaddon");
    }

    [Fact]
    public async Task UnmanagingPutsTheUsersConfigBackByteForByte()
    {
        var original = await Config.ReadAllBytesAsync();
        var loadout = await CreateLoadout();
        await InstallPlugin(loadout, "Mod.esp");
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
/// The game already has plugins in data-local and an openmw.cfg listing them: the cfg decides the order the app
/// starts from, and the first Apply writes the app's order back, leaving the user's own lines where they are.
/// </summary>
public class OpenMwSeedRoundTripTests(ITestOutputHelper helper) : AOpenMwGameTest<OpenMwSeedRoundTripTests>(helper)
{
    protected override string[] PluginsInDataLocal => ["A.esp", "B.esp"];

    // The cfg loads B before A.
    protected override string ConfigText => Install.Config("content=B.esp\ncontent=A.esp\n");

    [Fact]
    public async Task TheSeedComesFromTheOpenMwConfigAndTheFileIsWrittenInThatOrder()
    {
        var loadout = await CreateLoadout();

        await Synchronizer.Synchronize(Refresh(loadout));

        (await ContentLines()).Should().Equal("content=builtin.omwscripts", "content=Morrowind.esm", "content=Tribunal.esm", "content=Bloodmoon.esm", "content=B.esp", "content=A.esp");
    }
}
