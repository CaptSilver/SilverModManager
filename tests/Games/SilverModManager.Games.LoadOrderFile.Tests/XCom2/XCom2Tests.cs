using System.Text;
using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Games.LoadOrderFile.Tests.Fixtures;
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

    // The game ships the options file, so the install under test has one to replace.
    protected override IServiceCollection AddGameLocator(IServiceCollection services)
        => services.AddUniversalGameLocator(
            Definition.GameId,
            new Dictionary<RelativePath, byte[]>
            {
                [Definition.Executable] = [],
                [Definition.OrderFile] = Encoding.UTF8.GetBytes(ShippedOptions),
            },
            locationsFileSystem: IsolatedKnownFolders.Create);

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
    public async Task TheOptionsFileTheGameShipsIsBackedUpBeforeItIsReplaced()
    {
        var loadout = await CreateLoadout();

        await Synchronizer.Synchronize(Refresh(loadout));

        var original = FileIdentity.XxHash3(Encoding.UTF8.GetBytes(ShippedOptions).xxHash3());
        (await FileStore.HaveFile(original)).Should().BeTrue("a reset to the original game state puts the shipped file back");
    }
}
