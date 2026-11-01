using System.Text;
using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using System.Reactive.Linq;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Sdk.FileStore;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.LoadOrderFile.Tests.Fixtures;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.LoadOrderFile.Tests;

public class XCom2Tests(ITestOutputHelper outputHelper)
    : AXCom2RowTests<XCom2Tests>(outputHelper, LoadOrderFileGames.XCom2);

public class XCom2WarOfTheChosenTests(ITestOutputHelper outputHelper)
    : AXCom2RowTests<XCom2WarOfTheChosenTests>(outputHelper, LoadOrderFileGames.XCom2WarOfTheChosen);

/// <summary>
/// Both XCOM 2 games take a mod as a folder named after its .XComMod file under XComGame/Mods, and list the active
/// ones in XComGame/Config/DefaultModOptions.ini, a file the game ships.
/// </summary>
public abstract class AXCom2RowTests<TTest>(ITestOutputHelper outputHelper, LoadOrderFileGameDefinition definition)
    : ALoadOrderFileRowTests<TTest>(outputHelper, definition)
{
    private const string ShippedOptions = "[Engine.XComModOptions]\n";

    private string Root => Definition.GameId == LoadOrderFileGames.XCom2WarOfTheChosen.GameId ? "XCom2-WarOfTheChosen/" : "";

    protected override string[] ArchivePathsOf(string mod) => [$"{mod}/{mod}.XComMod", $"{mod}/Content/{mod}.upk"];

    // The depot lists the options file, as it lists every file the game ships, so a reset knows what to put back.
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services);
        services.Remove(services.Last(descriptor => descriptor.ServiceType == typeof(IFileHashesService)));
        var shipped = new GameFileRecord
        {
            Path = new GamePath(LocationId.Game, Definition.OrderFile),
            Size = Size.FromLong(Encoding.UTF8.GetByteCount(ShippedOptions)),
            Identity = FileIdentity.XxHash3(Encoding.UTF8.GetBytes(ShippedOptions).xxHash3()),
        };
        return services.AddSingleton<IFileHashesService>(provider => ActivatorUtilities.CreateInstance<HashesListingTheShippedFile>(provider, shipped));
    }

    /// <summary>The stubbed vanilla file list, plus one more file the game ships.</summary>
    private sealed class HashesListingTheShippedFile(IServiceProvider provider, IFileStore fileStore, TemporaryFileManager temporaryFiles, GameFileRecord shipped)
        : StubbedFileHasherService(provider, fileStore, temporaryFiles), IFileHashesService
    {
        public new IEnumerable<GameFileRecord> GetGameFiles(GameInstallation installation) => base.GetGameFiles(installation).Append(shipped);

        public new IEnumerable<GameFileRecord> GetGameFiles(LocatorIdsWithGameStore locatorIdsWithGameStore) => base.GetGameFiles(locatorIdsWithGameStore).Append(shipped);
    }

    // The game ships the options file, so the install under test has one to replace. It sits in a Steam library's
    // steamapps/common, where the Workshop content folder is its sibling.
    protected override IServiceCollection AddGameLocator(IServiceCollection services)
        => services.AddUniversalGameLocator(
            Definition.GameId,
            new Dictionary<RelativePath, byte[]>
            {
                [Definition.Executable] = [],
                [Definition.OrderFile] = Encoding.UTF8.GetBytes(ShippedOptions),
            },
            locationsFileSystem: IsolatedKnownFolders.Create,
            folderName: "steamapps/common/XCOM 2");

    private AbsolutePath GameFolder => GameInstallation.Locations[LocationId.Game].Path;

    private AbsolutePath OptionsFile => GameFolder.Combine(Definition.OrderFile);

    private AbsolutePath WorkshopFolder => GameFolder.Parent.Parent.Combine("workshop/content/268500");

    private async Task SubscribeToWorkshopMod(string id, string name)
    {
        var folder = WorkshopFolder.Combine(id);
        folder.CreateDirectory();
        await folder.Combine($"{name}.XComMod").WriteAllTextAsync("");
    }

    private async Task<LoadoutItemGroup.ReadOnly> InstalledMod(Loadout.ReadOnly loadout, string mod)
    {
        var archive = await AddArchiveNamed($"{mod}.zip", ArchivePathsOf(mod));
        return (await LoadoutManager.InstallItems([archive.AsLibraryFile().AsLibraryItem()], loadout.LoadoutId)).Single();
    }

    [Fact]
    public void ModsLiveBelowXComGameAndTheOptionsFileInItsConfigFolder()
    {
        Definition.ModFolder.ToString().Should().Be($"{Root}XComGame/Mods");
        Definition.OrderFile.ToString().Should().Be($"{Root}XComGame/Config/DefaultModOptions.ini");
    }

    [Fact]
    public async Task AModFolderInTheArchiveInstallsBelowMods()
    {
        var targets = await InstallTargets(["Cool/Cool.XComMod", "Cool/Content/cool.upk"]);

        targets.Should().Equal(OnGame($"{Root}XComGame/Mods/Cool/Content/cool.upk"), OnGame($"{Root}XComGame/Mods/Cool/Cool.XComMod"));
    }

    [Fact]
    public async Task AWrapperFolderWithAnotherNameIsReplacedByTheNameOfTheXComModFile()
    {
        var targets = await InstallTargets(["Download v2/Cool.XComMod", "Download v2/Content/cool.upk"]);

        targets.Should().Equal(OnGame($"{Root}XComGame/Mods/Cool/Content/cool.upk"), OnGame($"{Root}XComGame/Mods/Cool/Cool.XComMod"));
    }

    [Fact]
    public async Task AModAtTheTopOfTheArchiveGetsAFolderNamedAfterItsXComModFile()
    {
        var targets = await InstallTargets(["Cool.XComMod", "Content/cool.upk"], "download.zip");

        targets.Should().Equal(OnGame($"{Root}XComGame/Mods/Cool/Content/cool.upk"), OnGame($"{Root}XComGame/Mods/Cool/Cool.XComMod"));
    }

    [Fact]
    public async Task AReadmeBesideTheModFolderIsLeftOut()
    {
        var targets = await InstallTargets(["Cool/Cool.XComMod", "Cool/Content/cool.upk", "readme.txt"]);

        targets.Should().Equal(OnGame($"{Root}XComGame/Mods/Cool/Content/cool.upk"), OnGame($"{Root}XComGame/Mods/Cool/Cool.XComMod"));
    }

    [Fact]
    public async Task AnArchiveWithoutAnXComModFileIsNotSupported()
    {
        var targets = await InstallTargets(["Cool/Content/cool.upk"]);

        targets.Should().BeNull();
    }

    [Fact]
    public async Task ApplyReplacesTheOptionsFileTheGameShips()
    {
        var shipped = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, Definition.OrderFile));
        (await shipped.ReadAllTextAsync()).Should().Be(ShippedOptions);
        var loadout = await CreateLoadout();

        await Synchronizer.Synchronize(Refresh(loadout));

        (await shipped.ReadAllTextAsync()).Should().Be(Definition.Format.Render([]));
    }

    [Fact]
    public async Task TheOptionsFileTheGameShipsIsInTheFileStoreOnceItIsReplaced()
    {
        var loadout = await CreateLoadout();

        await Synchronizer.Synchronize(Refresh(loadout));

        var original = FileIdentity.XxHash3(Encoding.UTF8.GetBytes(ShippedOptions).xxHash3());
        (await FileStore.HaveFile(original)).Should().BeTrue();
    }

    [Fact]
    public async Task AResetToTheOriginalGameStatePutsTheShippedOptionsFileBack()
    {
        var loadout = await CreateLoadout();
        await InstalledMod(loadout, "Alpha");
        await Synchronizer.Synchronize(Refresh(loadout));
        (await OptionsFile.ReadAllTextAsync()).Should().Contain("ActiveMods=\"Alpha\"", "the apply replaced the shipped file");

        await Synchronizer.ResetToOriginalGameState(GameInstallation, GameInstallation.LocatorResult.LocatorIds.ToArray());

        (await OptionsFile.ReadAllTextAsync()).Should().Be(ShippedOptions);
    }

    [Fact]
    public async Task AWorkshopModTheOptionsFileListsStaysListedAfterApply()
    {
        await SubscribeToWorkshopMod("1122334455", "WorkshopMod");
        await OptionsFile.WriteAllTextAsync(ShippedOptions + "ActiveMods=\"WorkshopMod\"\n");
        var loadout = await CreateLoadout();
        await InstalledMod(loadout, "Alpha");

        await Synchronizer.Synchronize(Refresh(loadout));

        (await OptionsFile.ReadAllTextAsync()).Should().Be(Definition.Format.Render(["WorkshopMod", "Alpha"]));
    }

    [Fact]
    public async Task AWorkshopModStaysListedAcrossEveryApply()
    {
        await SubscribeToWorkshopMod("1122334455", "WorkshopMod");
        await OptionsFile.WriteAllTextAsync(ShippedOptions + "ActiveMods=\"WorkshopMod\"\n");
        var loadout = await CreateLoadout();
        await InstalledMod(loadout, "Alpha");
        await Synchronizer.Synchronize(Refresh(loadout));

        await Synchronizer.Synchronize(Refresh(loadout));

        (await OptionsFile.ReadAllTextAsync()).Should().Be(Definition.Format.Render(["WorkshopMod", "Alpha"]));
    }

    [Fact]
    public async Task AWorkshopModTheOptionsFileDoesNotListStaysOff()
    {
        await SubscribeToWorkshopMod("1122334455", "WorkshopMod");
        var loadout = await CreateLoadout();

        await Synchronizer.Synchronize(Refresh(loadout));

        (await OptionsFile.ReadAllTextAsync()).Should().Be(Definition.Format.Render([]));
    }

    [Fact]
    public async Task AListedModThatIsNeitherInTheLoadoutNorSubscribedToIsDropped()
    {
        await SubscribeToWorkshopMod("1122334455", "WorkshopMod");
        await OptionsFile.WriteAllTextAsync(ShippedOptions + "ActiveMods=\"Removed\"\nActiveMods=\"WorkshopMod\"\n");
        var loadout = await CreateLoadout();

        await Synchronizer.Synchronize(Refresh(loadout));

        (await OptionsFile.ReadAllTextAsync()).Should().Be(Definition.Format.Render(["WorkshopMod"]));
    }

    [Fact]
    public async Task AWorkshopModWithTheNameOfALoadoutModIsListedOnce()
    {
        await SubscribeToWorkshopMod("1122334455", "Alpha");
        await OptionsFile.WriteAllTextAsync(ShippedOptions + "ActiveMods=\"Alpha\"\n");
        var loadout = await CreateLoadout();
        await InstalledMod(loadout, "Alpha");

        await Synchronizer.Synchronize(Refresh(loadout));

        (await OptionsFile.ReadAllTextAsync()).Should().Be(Definition.Format.Render(["Alpha"]));
    }

    [Fact]
    public async Task ApplyLeavesNothingToApplyAgainWhileAWorkshopModIsListed()
    {
        await SubscribeToWorkshopMod("1122334455", "WorkshopMod");
        await OptionsFile.WriteAllTextAsync(ShippedOptions + "ActiveMods=\"WorkshopMod\"\n");
        var loadout = await CreateLoadout();
        await InstalledMod(loadout, "Alpha");
        await Synchronizer.Synchronize(Refresh(loadout));

        var status = await (await SynchronizerService.StatusForLoadout(loadout.LoadoutId))
            .Where(state => state != LoadoutSynchronizerState.Pending).Timeout(TimeSpan.FromSeconds(30)).FirstAsync();

        status.Should().Be(LoadoutSynchronizerState.Current);
    }

    [Fact]
    public async Task ALinuxBuildOfTheGameIsRefusedBeforeAnythingIsWritten()
    {
        var loadout = await CreateLoadout();
        await InstalledMod(loadout, "Alpha");
        var nativeLauncher = GameFolder.Combine(Definition == LoadOrderFileGames.XCom2WarOfTheChosen ? "XCOM2WotC/XCOM2WotC.sh" : "XCOM2.sh");
        nativeLauncher.Parent.CreateDirectory();
        await nativeLauncher.WriteAllTextAsync("#!/bin/sh");
        var before = await OptionsFile.ReadAllTextAsync();

        var apply = async () => await Synchronizer.Synchronize(Refresh(loadout));

        (await apply.Should().ThrowAsync<UserFacingException>()).Which.Message.Should().Contain("Proton");
        (GameFolder / Definition.ModFolder / "Alpha").DirectoryExists().Should().BeFalse();
        (await OptionsFile.ReadAllTextAsync()).Should().Be(before);
    }
}
