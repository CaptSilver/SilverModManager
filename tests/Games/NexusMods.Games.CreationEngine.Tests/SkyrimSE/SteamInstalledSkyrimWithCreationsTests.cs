using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.CreationEngine.Tests.Creations;
using NexusMods.Games.TestFramework;
using NexusMods.Games.TestFramework.Helpers;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using Xunit;

namespace NexusMods.Games.CreationEngine.Tests.SkyrimSE;

/// <summary>
/// Skyrim Special Edition with Creation Club content the game downloaded itself: a catalog in AppData, manifests
/// in the Creations folder and the plugins and archives they name in Data. Taken through the real service graph.
/// </summary>
public class SteamInstalledSkyrimWithCreationsTests(ITestOutputHelper helper)
    : ASteamInstalledGameTest<SteamInstalledSkyrimWithCreationsTests, CreationEngine.SkyrimSE.SkyrimSE>(helper)
{
    private const uint AppId = 489830;
    private const uint GameDepotId = 489831;
    private const ulong GameManifestId = 5000000000000000001;

    private static readonly DiagnosticId NonVanillaId = new("NexusMods.Games.FileHashes", number: 3);

    private static readonly string[] GardenFiles = ["Data/ccinvented001-garden.esm", "Data/ccinvented001-garden.bsa"];
    private static readonly string[] ShedFiles = ["Data/ccinvented002-shed.esm", "Data/ccinvented002-shed.bsa"];
    private static readonly string[] ChestFiles = ["Data/ccinvented003-chest.esp", "Data/ccinvented003-chest.bsa"];

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services).AddCreationEngine();

    protected override SteamInstall InstallGame(SteamLibraryFixture library)
    {
        var game = new SteamDepot(GameDepotId, GameManifestId,
        [
            new SteamFile("SkyrimSE.exe", "skyrim executable"),
            new SteamFile("Data/Skyrim.esm", SkyrimPluginFile.Bytes("Skyrim.esm")),
            new SteamFile("Data/Update.esm", SkyrimPluginFile.Bytes("Update.esm", "Skyrim.esm")),
            new SteamFile("Data/Dawnguard.esm", SkyrimPluginFile.Bytes("Dawnguard.esm", "Skyrim.esm")),
        ]);
        var install = library.AddGame(AppId, "Skyrim Special Edition", "The Elder Scrolls V: Skyrim Special Edition", 16000000, game);

        // The Creation Club list names the garden only; the shed and the chest are Creations it does not list.
        SteamLibraryFixture.WriteExtra(install, "Skyrim.ccc", "ccinvented001-garden.esm\n");
        SteamLibraryFixture.WriteExtra(install, GardenFiles[0], SkyrimPluginFile.Bytes("ccinvented001-garden.esm", "Skyrim.esm"));
        SteamLibraryFixture.WriteExtra(install, ShedFiles[0], SkyrimPluginFile.Bytes("ccinvented002-shed.esm", "Skyrim.esm"));
        SteamLibraryFixture.WriteExtra(install, ChestFiles[0], SkyrimPluginFile.Bytes("ccinvented003-chest.esp", "Skyrim.esm"));
        foreach (var archive in new[] { GardenFiles[1], ShedFiles[1], ChestFiles[1] })
            SteamLibraryFixture.WriteExtra(install, archive, $"archive {archive}");

        // A mod file the user put there, so the non-vanilla notice has something to list.
        SteamLibraryFixture.WriteExtra(install, "Data/Present.esp", SkyrimPluginFile.Bytes("Present.esp", "Skyrim.esm"));
        return install;
    }

    private AbsolutePath AppData => GameInstallation.Locations[LocationId.AppData].Path;

    private void WriteRecords()
    {
        CreationsRecords.WriteCatalog(AppData,
            ("CSV2_1", "Invented Garden", ["ccinvented001-garden.esm", "ccinvented001-garden.bsa"]),
            ("CSV2_2", "Invented Shed", ["ccinvented002-shed.esm", "ccinvented002-shed.bsa"]));
        CreationsRecords.WriteManifest(GameFolder, "ccinvented003-1700000000.manifest", "data/ccinvented003-chest.esp", "data/ccinvented003-chest.bsa");
    }

    private AbsolutePath OnDisk(string gameRelative) => GameFolder / gameRelative;

    private GameInstalledItem.ReadOnly[] Items(Loadout.ReadOnly loadout) => CreationsLoadout.Items(Connection.Db, loadout.LoadoutId);

    private string[] ExternalChanges(Loadout.ReadOnly loadout) => CreationsLoadout.ExternalChanges(Connection.Db, loadout.LoadoutId);

    private async Task Apply(Loadout.ReadOnly loadout) => await Synchronizer.Synchronize(Refresh(loadout));

    private async Task<string[]> PluginsTxtLines()
    {
        var text = await GameInstallation.Locations.ToAbsolutePath(Game.PluginsFile).ReadAllTextAsync();
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private async Task<Loadout.ReadOnly> AppliedLoadout()
    {
        WriteRecords();
        var loadout = await CreateLoadout();
        await Apply(loadout);
        return Refresh(loadout);
    }

    [Fact]
    public async Task EachCatalogEntryAndEachManifestOnlyCreationIsAnItem()
    {
        var loadout = await AppliedLoadout();

        var items = Items(loadout);
        items.Select(i => i.SourceId).Should().BeEquivalentTo("CSV2_1", "CSV2_2", "ccinvented003");
        items.Should().OnlyContain(i => i.Source == "SkyrimCreation");
        items.Single(i => i.SourceId == "CSV2_1").AsLoadoutItemGroup().AsLoadoutItem().Name.Should().Be("Invented Garden");
        items.Single(i => i.SourceId == "CSV2_2").AsLoadoutItemGroup().AsLoadoutItem().Name.Should().Be("Invented Shed");
    }

    [Fact]
    public async Task NeitherTheCreationFilesNorTheRecordsAreExternalChanges()
    {
        var loadout = await AppliedLoadout();

        var external = ExternalChanges(loadout);
        external.Should().Contain("Data/Present.esp", "a file the user added is still an external change");
        external.Should().NotContain(path => path.Contains("ccinvented", StringComparison.OrdinalIgnoreCase));
        external.Should().NotContain(path => path.Contains("ContentCatalog", StringComparison.OrdinalIgnoreCase));
        external.Should().NotContain(path => path.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TheNonVanillaNoticeListsNeitherTheCreationFilesNorTheRecords()
    {
        var loadout = await AppliedLoadout();

        var diagnostics = await RunEveryEmitter(loadout);

        var notice = Describe(diagnostics.Where(d => d.Id == NonVanillaId).Should().ContainSingle().Subject);
        notice.Should().Contain("Present.esp");
        notice.Should().NotContain("ccinvented", "the game installed these itself");
        notice.Should().NotContain(".manifest");
    }

    [Fact]
    public async Task PluginsTxtStarsTheCreationsTheCreationClubListDoesNotName()
    {
        await AppliedLoadout();

        var lines = await PluginsTxtLines();
        lines.Should().Contain("*ccinvented002-shed.esm");
        lines.Should().Contain("*ccinvented003-chest.esp");
        lines.Should().NotContain(line => line.Contains("ccinvented001-garden.esm"), "an early loader is not written to the list");
    }

    [Fact]
    public async Task ApplyCleanFolderAndResetLeaveTheCreationsAndTheirRecordsOnDisk()
    {
        var loadout = await AppliedLoadout();
        string[] kept = [.. GardenFiles, .. ShedFiles, .. ChestFiles, "Creations/ccinvented003-1700000000.manifest"];

        async Task AssertKept(string step)
        {
            foreach (var file in kept) OnDisk(file).FileExists.Should().BeTrue($"{file} survives {step}");
            (AppData / "ContentCatalog.txt").FileExists.Should().BeTrue($"the catalog survives {step}");
        }

        await Apply(loadout);
        await AssertKept("an Apply");

        await LoadoutManager.CleanGameFolder(Refresh(loadout));
        await AssertKept("Clean folder");

        await Synchronizer.ResetToOriginalGameState(GameInstallation, GameInstallation.LocatorResult.LocatorIds.ToArray());
        await AssertKept("a Reset");
    }

    [Fact]
    public async Task AnItemKeepsTheFilesLeftAndGoesWhenTheLastOneIsGone()
    {
        var loadout = await AppliedLoadout();

        OnDisk(GardenFiles[0]).Delete();
        await Apply(loadout);
        var garden = Items(Refresh(loadout)).Single(i => i.SourceId == "CSV2_1");
        garden.AsLoadoutItemGroup().Children.Should().ContainSingle("the archive is still there");

        OnDisk(GardenFiles[1]).Delete();
        await Apply(loadout);
        Items(Refresh(loadout)).Select(i => i.SourceId).Should().NotContain("CSV2_1");
    }

    [Fact]
    public async Task ACatalogEntryThatGoesAwayLeavesItsFilesAsExternalChangesOnDisk()
    {
        var loadout = await AppliedLoadout();

        CreationsRecords.WriteCatalog(AppData, ("CSV2_2", "Invented Shed", ["ccinvented002-shed.esm", "ccinvented002-shed.bsa"]));
        await Apply(loadout);

        Items(Refresh(loadout)).Select(i => i.SourceId).Should().NotContain("CSV2_1");
        ExternalChanges(Refresh(loadout)).Should().Contain(GardenFiles);
        foreach (var file in GardenFiles) OnDisk(file).FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task CreationsFirstSeenAsExternalChangesAreHandedBackOnceTheCatalogNamesThem()
    {
        var loadout = await CreateLoadout();
        await Apply(loadout);
        ExternalChanges(Refresh(loadout)).Should().Contain(GardenFiles, "without a record nothing says the game installed them");

        WriteRecords();
        await Apply(loadout);

        var external = ExternalChanges(Refresh(loadout));
        external.Should().NotContain(path => path.Contains("ccinvented", StringComparison.OrdinalIgnoreCase));
        Items(Refresh(loadout)).Should().HaveCount(3);
        foreach (var file in (string[])[.. GardenFiles, .. ShedFiles, .. ChestFiles]) OnDisk(file).FileExists.Should().BeTrue();
    }
}
