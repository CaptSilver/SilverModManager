using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.LoadOrderFile.Tests.Fixtures;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.LoadOrderFile.Tests;

/// <summary>
/// Starsector takes a mod as a folder named after the <c>id</c> in its <c>mod_info.json</c> and lists the enabled ones
/// in <c>mods/enabled_mods.json</c> in the game folder. Whether the game reads that file's order is not known, so the
/// Load Order page is hidden.
/// </summary>
public class StarsectorTests(ITestOutputHelper outputHelper)
    : ALoadOrderFileRowTests<StarsectorTests>(outputHelper, LoadOrderFileGames.Starsector)
{
    protected override (string Path, string Content)[] ArchiveFilesOf(string mod)
        => [($"{mod}/mod_info.json", ModInfo(mod)), ($"{mod}/data/x.csv", "")];

    private static string ModInfo(string id) => $"{{\"id\":\"{id}\",\"name\":\"{id}\"}}";

    private AbsolutePath GameFolder => GameInstallation.Locations[LocationId.Game].Path;

    private async Task<string[]> ListedAfterApply(Loadout.ReadOnly loadout)
    {
        await Synchronizer.Synchronize(Refresh(loadout));
        return Definition.Format.Listed(await OrderFileOnDisk.ReadAllTextAsync()).ToArray();
    }

    private async Task PutPreexistingModOnDisk()
    {
        var info = GameFolder.Combine("mods/OldFolder/mod_info.json");
        info.Parent.CreateDirectory();
        await info.WriteAllTextAsync(ModInfo("old_mod"));
    }

    [Fact]
    public void TheOrderFileIsInTheGameFoldersModFolder()
    {
        Definition.OrderFile.ToGamePath().Should().Be(new GamePath(LocationId.Game, "mods/enabled_mods.json"));
    }

    [Fact]
    public async Task TheFolderIsNamedAfterTheModId()
    {
        var archive = await AddArchiveWithContents("download.zip", ("Cool Mod v1.2/mod_info.json", ModInfo("cool_mod")), ("Cool Mod v1.2/data/x.csv", ""));

        var targets = await InstallTargets(archive);

        targets.Should().Equal(OnGame("mods/cool_mod/data/x.csv"), OnGame("mods/cool_mod/mod_info.json"));
    }

    [Fact]
    public async Task AModInfoWithCommentsAndATrailingCommaStillNamesTheFolder()
    {
        var archive = await AddArchiveWithContents("download.zip", ("Cool/mod_info.json", "{\n  // a comment\n  \"id\": \"cool_mod\",\n}"), ("Cool/data/x.csv", ""));

        var targets = await InstallTargets(archive);

        targets.Should().Contain(OnGame("mods/cool_mod/mod_info.json"));
    }

    [Fact]
    public async Task AnArchiveWithoutAnIdIsRefused()
    {
        var archive = await AddArchiveWithContents("download.zip", ("Cool/mod_info.json", "{\"name\":\"x\"}"), ("Cool/data/x.csv", ""));

        var (targets, reason) = await TryInstall(archive);

        targets.Should().BeNull();
        reason.Should().Contain("has no id");
    }

    [Fact]
    public async Task AnArchiveWithTwoModsIsRefusedNamingTheCount()
    {
        var archive = await AddArchiveWithContents("pack.zip", ("One/mod_info.json", ModInfo("one")), ("Two/mod_info.json", ModInfo("two")));

        var (targets, reason) = await TryInstall(archive);

        targets.Should().BeNull();
        reason.Should().Contain("2 mods");
    }

    [Fact]
    public async Task TheFileListsAnInstalledModByItsId()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Alpha");

        (await ListedAfterApply(loadout)).Should().Equal("Alpha");
    }

    [Fact]
    public async Task APreexistingModFolderIsListedByItsIdWhenTheFileNamesIt()
    {
        await PutPreexistingModOnDisk();
        await OrderFileOnDisk.WriteAllTextAsync(Definition.Format.Render(["old_mod"], null));
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Alpha");

        (await ListedAfterApply(loadout)).Should().Equal("Alpha", "old_mod");
    }

    [Fact]
    public async Task APreexistingModFolderStaysOffWhenTheFileDoesNotNameIt()
    {
        await PutPreexistingModOnDisk();
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Alpha");

        (await ListedAfterApply(loadout)).Should().Equal("Alpha");
    }

    // Saves and the log are the game's own output: ingesting them would turn every play session into an external change.
    [Theory]
    [InlineData("saves/save_one/descriptor.xml", true)]
    [InlineData("starsector-core/starsector.log", true)]
    [InlineData("mods/alpha/mod_info.json", false)]
    [InlineData("starsector-core/data/config/settings.json", false)]
    public void SavesAndTheLogAreLeftOutOfTheLoadout(string path, bool filtered)
    {
        Synchronizer.GamePathFilter.ShouldFilter(new GamePath(LocationId.Game, path)).Should().Be(filtered);
    }

    [Fact]
    public void TheLoadOrderPageIsHidden()
    {
        LoadOrderPage.IsShown(Game).Should().BeFalse();
        DefaultPriorityOrderSink.PriorityPage(Game).Should().Be("File Conflicts");
    }

    [Fact]
    public void TheGameHasNoStoreIds()
    {
        Definition.Stores.SteamAppIds.Should().BeEmpty();
        Definition.Stores.GOGProductIds.Should().BeEmpty();
    }

    [Fact]
    public void TheLocationsAreTheGameFolderOnly()
    {
        GameInstallation.Locations.Keys.Should().BeEquivalentTo([LocationId.Game]);
    }
}
