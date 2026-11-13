using System.Reactive.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Generic.Simple;
using SilverModManager.Games.LoadOrderFile.Tests.Fixtures;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.LoadOrderFile.Tests;

/// <summary>
/// RimWorld takes a mod as a folder named after the <c>packageId</c> in its <c>About/About.xml</c> and lists the active
/// ones in <c>ModsConfig.xml</c> in the game's LocalLow config folder (inside the Proton prefix for the Windows build).
/// </summary>
public class RimWorldTests(ITestOutputHelper outputHelper)
    : ALoadOrderFileRowTests<RimWorldTests>(outputHelper, LoadOrderFileGames.RimWorld)
{
    private const string Prefs = "<Prefs />";

    protected override (string Path, string Content)[] ArchiveFilesOf(string mod)
        => [($"{mod}/About/About.xml", About(mod)), ($"{mod}/Defs/x.xml", "")];

    protected override string ListedNameOf(string mod) => mod.ToLowerInvariant();

    private static string About(string packageId, string rules = "") => $"<ModMetaData><name>{packageId}</name><packageId>{packageId}</packageId>{rules}</ModMetaData>";

    // The Workshop folder is the game's sibling in a Steam library, so the game sits in steamapps/common.
    protected override IServiceCollection AddGameLocator(IServiceCollection services)
        => services.AddUniversalGameLocator(
            Definition.GameId,
            new Dictionary<RelativePath, byte[]> { [Definition.Executable] = [] },
            locationsFileSystem: IsolatedKnownFolders.Create,
            platform: System.Runtime.InteropServices.OSPlatform.Windows,
            folderName: "steamapps/common/RimWorld");

    private AbsolutePath GameFolder => GameInstallation.Locations[LocationId.Game].Path;

    private AbsolutePath ConfigFolder => GameInstallation.Locations[SimpleLocations.LocalAppDataLow].Path;

    private async Task WriteToGame(string relative, string content)
    {
        var file = GameFolder.Combine(relative);
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync(content);
    }

    private Task AddCore() => WriteToGame("Data/Core/About/About.xml", About("Ludeon.RimWorld"));

    private async Task<LoadoutItemGroup.ReadOnly> InstallArchive(Loadout.ReadOnly loadout, string name, params (string Path, string Content)[] files)
    {
        var archive = await AddArchiveWithContents($"{name}.zip", files);
        return (await LoadoutManager.InstallItems([archive.AsLibraryFile().AsLibraryItem()], loadout.LoadoutId)).Single();
    }

    private async Task<string[]> ListedAfterApply(Loadout.ReadOnly loadout)
    {
        await Synchronizer.Synchronize(Refresh(loadout));
        return Definition.Format.Listed(await OrderFileOnDisk.ReadAllTextAsync()).ToArray();
    }

    [Fact]
    public void TheConfigLocationIsInsideThePrefixesLocalLow()
    {
        ConfigFolder.ToString().Should().EndWith("AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/Config");
    }

    [Fact]
    public async Task TheFolderIsNamedAfterThePackageId()
    {
        var archive = await AddArchiveWithContents("download.zip", ("Harmony v2.3.6/About/About.xml", About("Brrainz.Harmony")), ("Harmony v2.3.6/Defs/x.xml", ""));

        var targets = await InstallTargets(archive);

        targets.Should().Equal(OnGame("Mods/brrainz.harmony/About/About.xml"), OnGame("Mods/brrainz.harmony/Defs/x.xml"));
    }

    [Fact]
    public async Task AnArchiveWithoutAPackageIdIsRefused()
    {
        var archive = await AddArchiveWithContents("download.zip", ("Harmony/About/About.xml", "<ModMetaData><name>x</name></ModMetaData>"), ("Harmony/Defs/x.xml", ""));

        var (targets, reason) = await TryInstall(archive);

        targets.Should().BeNull();
        reason.Should().Contain("has no packageId");
    }

    [Fact]
    public async Task AnArchiveWithTwoModsIsRefusedNamingTheCount()
    {
        var archive = await AddArchiveWithContents("pack.zip", ("One/About/About.xml", About("a.one")), ("Two/About/About.xml", About("b.two")));

        var (targets, reason) = await TryInstall(archive);

        targets.Should().BeNull();
        reason.Should().Contain("2 mods");
    }

    [Fact]
    public async Task CoreIsWrittenFirstOnAFreshFile()
    {
        await AddCore();
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Alpha");

        (await ListedAfterApply(loadout)).Should().Equal("ludeon.rimworld", "alpha");
    }

    [Fact]
    public async Task AnExpansionTheFileListsStaysAndOneItDoesNotStaysOff()
    {
        await AddCore();
        await WriteToGame("Data/Royalty/About/About.xml", About("Ludeon.RimWorld.Royalty"));
        await WriteToGame("Data/Ideology/About/About.xml", About("Ludeon.RimWorld.Ideology"));
        ConfigFolder.CreateDirectory();
        await OrderFileOnDisk.WriteAllTextAsync(Definition.Format.Render(["ludeon.rimworld", "ludeon.rimworld.royalty"], null));
        var loadout = await CreateLoadout();

        (await ListedAfterApply(loadout)).Should().Equal("ludeon.rimworld", "ludeon.rimworld.royalty");
    }

    [Fact]
    public async Task ThePlayersVersionAndKnownExpansionsSurviveApply()
    {
        ConfigFolder.CreateDirectory();
        await OrderFileOnDisk.WriteAllTextAsync("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ModsConfigData>\n  <version>1.5.4297 rev435</version>\n  <activeMods>\n    <li>ludeon.rimworld</li>\n  </activeMods>\n  <knownExpansions>\n    <li>ludeon.rimworld.royalty</li>\n  </knownExpansions>\n</ModsConfigData>");
        await AddCore();
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Alpha");

        await Synchronizer.Synchronize(Refresh(loadout));

        var text = await OrderFileOnDisk.ReadAllTextAsync();
        text.Should().Contain("<version>1.5.4297 rev435</version>").And.Contain("<li>ludeon.rimworld.royalty</li>");
        Definition.Format.Listed(text).Should().Equal("ludeon.rimworld", "alpha");
    }

    [Fact]
    public async Task AFirstApplyOfAManagedHarmonyWritesItAboveCore()
    {
        await AddCore();
        var loadout = await CreateLoadout();
        await InstallArchive(loadout, "Harmony", ("Harmony/About/About.xml", About("Brrainz.Harmony", "<loadBefore><li>Ludeon.RimWorld</li></loadBefore>")), ("Harmony/Defs/x.xml", ""));

        (await ListedAfterApply(loadout)).Should().Equal("brrainz.harmony", "ludeon.rimworld");
    }

    private async Task PutPreexistingModOnDisk() => await WriteToGame("Mods/Old/About/About.xml", About("Old.Mod"));

    [Fact]
    public async Task APreexistingModFolderIsListedByItsPackageIdWhenTheFileNamesIt()
    {
        await PutPreexistingModOnDisk();
        ConfigFolder.CreateDirectory();
        await OrderFileOnDisk.WriteAllTextAsync(Definition.Format.Render(["old.mod"], null));
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Alpha");

        (await ListedAfterApply(loadout)).Should().Equal("old.mod", "alpha");
    }

    [Fact]
    public async Task APreexistingModFolderStaysOffWhenTheFileDoesNotNameIt()
    {
        await PutPreexistingModOnDisk();
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Alpha");

        (await ListedAfterApply(loadout)).Should().Equal("alpha");
    }

    [Fact]
    public async Task PrefsXmlBesideTheOrderFileIsNotAnExternalChange()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Alpha");
        await Synchronizer.Synchronize(Refresh(loadout));

        await ConfigFolder.Combine("Prefs.xml").WriteAllTextAsync(Prefs);

        var status = await (await SynchronizerService.StatusForLoadout(loadout.LoadoutId))
            .Where(state => state != LoadoutSynchronizerState.Pending).Timeout(TimeSpan.FromSeconds(30)).FirstAsync();
        status.Should().Be(LoadoutSynchronizerState.Current);
    }

    [Fact]
    public async Task AReadOnlyConfigFolderIsRefusedBeforeAnyWrite()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Alpha");
        ConfigFolder.CreateDirectory();
        File.SetUnixFileMode(ConfigFolder.ToString(), UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            var apply = async () => await Synchronizer.Synchronize(Refresh(loadout));

            await apply.Should().ThrowAsync<UserFacingException>();
            GameFolder.Combine(Definition.ModFolder).Combine("alpha").DirectoryExists().Should().BeFalse("nothing is written when the order file cannot be");
        }
        finally
        {
            File.SetUnixFileMode(ConfigFolder.ToString(), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
