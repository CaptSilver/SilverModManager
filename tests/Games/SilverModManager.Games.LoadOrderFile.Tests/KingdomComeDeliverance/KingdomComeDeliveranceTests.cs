using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Games.LoadOrderFile.Tests.Fixtures;

namespace SilverModManager.Games.LoadOrderFile.Tests;

public class KingdomComeDeliveranceTests(ITestOutputHelper outputHelper)
    : AKingdomComeRowTests<KingdomComeDeliveranceTests>(outputHelper, LoadOrderFileGames.KingdomComeDeliverance)
{
    [Fact]
    public async Task TheFolderKeepsTheArchivesNameWhateverTheManifestsModIdSays()
    {
        var archive = await AddArchiveWithContents("MyMod.zip", ("MyMod v1.2/mod.manifest", ManifestWithModId("mymod")), ("MyMod v1.2/Data/a.pak", "pak"));

        var targets = await InstallTargets(archive);

        targets.Should().Equal(OnGame("Mods/MyMod v1.2/Data/a.pak"), OnGame("Mods/MyMod v1.2/mod.manifest"));
    }
}

/// <summary>
/// The second game's mod_order.txt lists each mod by the id in its manifest (Mod Organizer 2's plugin takes the
/// manifest's modid, then its name, then the folder), so a mod goes in a folder of that name.
/// </summary>
public class KingdomComeDeliverance2Tests(ITestOutputHelper outputHelper)
    : AKingdomComeRowTests<KingdomComeDeliverance2Tests>(outputHelper, LoadOrderFileGames.KingdomComeDeliverance2)
{
    private async Task<string[]?> InstallArchive(string archiveFileName, string manifest, string manifestFolder = "")
    {
        var archive = await AddArchiveWithContents(archiveFileName, ($"{manifestFolder}mod.manifest", manifest), ($"{manifestFolder}Data/a.pak", "pak"));
        return await InstallTargets(archive);
    }

    [Fact]
    public async Task TheFolderIsNamedAfterTheManifestsModIdNotTheArchivesFolder()
    {
        var targets = await InstallArchive("MyMod.zip", ManifestWithModId("mymod"), "MyMod v1.2/");

        targets.Should().Equal(OnGame("Mods/mymod/Data/a.pak"), OnGame("Mods/mymod/mod.manifest"));
    }

    [Fact]
    public async Task AModAtTheTopOfTheArchiveIsNamedAfterItsModIdToo()
    {
        var targets = await InstallArchive("download.zip", ManifestWithModId("mymod"));

        targets.Should().Equal(OnGame("Mods/mymod/Data/a.pak"), OnGame("Mods/mymod/mod.manifest"));
    }

    [Fact]
    public async Task AManifestWithoutAModIdNamesTheFolderAfterTheMod()
    {
        var targets = await InstallArchive("download.zip", "<kcd_mod><info><name>Cool Mod</name></info></kcd_mod>");

        targets.Should().Equal(OnGame("Mods/Cool Mod/Data/a.pak"), OnGame("Mods/Cool Mod/mod.manifest"));
    }

    [Fact]
    public async Task AManifestThatNamesNothingLeavesTheArchivesFolderName()
    {
        var targets = await InstallArchive("download.zip", "<kcd_mod><info/></kcd_mod>", "Cool/");

        targets.Should().Equal(OnGame("Mods/Cool/Data/a.pak"), OnGame("Mods/Cool/mod.manifest"));
    }

    [Fact]
    public async Task AManifestThatIsNotXmlLeavesTheArchivesFolderName()
    {
        var targets = await InstallArchive("download.zip", "not xml at all", "Cool/");

        targets.Should().Equal(OnGame("Mods/Cool/Data/a.pak"), OnGame("Mods/Cool/mod.manifest"));
    }

    [Fact]
    public async Task AModIdThatIsNoFolderNameLeavesTheArchivesFolderName()
    {
        var targets = await InstallArchive("download.zip", ManifestWithModId("../escape"), "Cool/");

        targets.Should().Equal(OnGame("Mods/Cool/Data/a.pak"), OnGame("Mods/Cool/mod.manifest"));
    }
}

/// <summary>Both Kingdom Come games take a mod as a folder under Mods, named for the mod, and list those folders in mod_order.txt.</summary>
public abstract class AKingdomComeRowTests<TTest>(ITestOutputHelper outputHelper, LoadOrderFileGameDefinition definition)
    : ALoadOrderFileRowTests<TTest>(outputHelper, definition)
{
    protected override (string Path, string Content)[] ArchiveFilesOf(string mod)
        => [($"{mod}/mod.manifest", $"{mod}/mod.manifest"), ($"{mod}/Data/{mod}.pak", $"{mod}/Data/{mod}.pak")];

    protected static string ManifestWithModId(string modId)
        => $"<?xml version=\"1.0\" encoding=\"utf-8\"?><kcd_mod><info><name>Some Name</name><modid>{modId}</modid></info></kcd_mod>";

    [Fact]
    public void TheGameHasNoManifestDiagnostics()
    {
        Game.DiagnosticEmitters.OfType<WorkshopDuplicateEmitter>().Should().BeEmpty("the game has no Steam Workshop source");
        Game.DiagnosticEmitters.OfType<ManifestOrderEmitter>().Should().BeEmpty("the game has no manifest reader");
    }

    [Fact]
    public void ModsLiveInTheModsFolderAndTheOrderFileBesideThem()
    {
        Definition.ModFolder.ToString().Should().Be("Mods");
        Definition.OrderFile.Path.ToString().Should().Be("Mods/mod_order.txt");
    }

    [Fact]
    public void NothingInTheGameFolderIsFiltered()
    {
        Synchronizer.GamePathFilter.ShouldFilter(new GamePath(LocationId.Game, "Mods/x/Data/a.pak")).Should().BeFalse();
        Synchronizer.GamePathFilter.ShouldFilter(new GamePath(LocationId.Game, "Mods/mod_order.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task AnApplyWithNoModsCreatesTheOrderFilesFolder()
    {
        var gameFolder = GameInstallation.Locations[LocationId.Game].Path;
        (gameFolder / "Mods").DirectoryExists().Should().BeFalse();
        var loadout = await CreateLoadout();

        await Synchronizer.Synchronize(Refresh(loadout));

        (gameFolder / "Mods/mod_order.txt").FileExists.Should().BeTrue();
    }

    private async Task<Loadout.ReadOnly> LoadoutWithAPreexistingMod(string? orderFile)
    {
        var mods = GameInstallation.Locations[LocationId.Game].Path / "Mods";
        (mods / "Old/Data").CreateDirectory();
        await (mods / "Old/Data/old.pak").WriteAllTextAsync("pak");
        if (orderFile is not null) await (mods / "mod_order.txt").WriteAllTextAsync(orderFile);
        return await CreateLoadout();
    }

    [Fact]
    public async Task APreexistingModTheFileListsAsOffStaysOff()
    {
        var loadout = await LoadoutWithAPreexistingMod("Alpha");
        await InstallMod(loadout, "Alpha");

        await Synchronizer.Synchronize(Refresh(loadout));

        (await OrderFileOnDisk.ReadAllTextAsync()).Should().Be("Alpha");
    }

    [Fact]
    public async Task APreexistingModIsListedWhenTheFileNamesIt()
    {
        var loadout = await LoadoutWithAPreexistingMod("Old");
        await InstallMod(loadout, "Alpha");

        await Synchronizer.Synchronize(Refresh(loadout));

        (await OrderFileOnDisk.ReadAllTextAsync()).Should().Be("Old\nAlpha");
    }

    [Fact]
    public async Task APreexistingModIsListedWhenThereIsNoFileYet()
    {
        var loadout = await LoadoutWithAPreexistingMod(null);
        await InstallMod(loadout, "Alpha");

        await Synchronizer.Synchronize(Refresh(loadout));

        (await OrderFileOnDisk.ReadAllTextAsync()).Should().Be("Old\nAlpha");
    }

    [Fact]
    public async Task AModFolderInTheArchiveInstallsBelowMods()
    {
        var targets = await InstallTargets(["Cool/mod.manifest", "Cool/Data/cool.pak"]);

        targets.Should().Equal(OnGame("Mods/Cool/Data/cool.pak"), OnGame("Mods/Cool/mod.manifest"));
    }

    [Fact]
    public async Task AModsFolderInTheArchiveIsNotNestedAgain()
    {
        var targets = await InstallTargets(["Mods/Cool/mod.manifest", "Mods/Cool/Data/cool.pak"]);

        targets.Should().Equal(OnGame("Mods/Cool/Data/cool.pak"), OnGame("Mods/Cool/mod.manifest"));
    }

    [Fact]
    public async Task AModAtTheTopOfTheArchiveGetsAFolderNamedAfterTheArchive()
    {
        var targets = await InstallTargets(["mod.manifest", "Data/cool.pak"], "Cool Mod.zip");

        targets.Should().Equal(OnGame("Mods/Cool Mod/Data/cool.pak"), OnGame("Mods/Cool Mod/mod.manifest"));
    }

    [Fact]
    public async Task AReadmeBesideTheModFolderIsLeftOut()
    {
        var targets = await InstallTargets(["Cool/mod.manifest", "Cool/Data/cool.pak", "readme.txt"]);

        targets.Should().Equal(OnGame("Mods/Cool/Data/cool.pak"), OnGame("Mods/Cool/mod.manifest"));
    }

    [Fact]
    public async Task AnArchiveWithoutAModManifestIsNotSupported()
    {
        var targets = await InstallTargets(["Cool/Data/cool.pak"]);

        targets.Should().BeNull();
    }
}
