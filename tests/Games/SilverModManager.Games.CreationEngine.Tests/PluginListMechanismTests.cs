using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Games.CreationEngine.PluginLists;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.StandardGameLocators.TestHelpers;

namespace NexusMods.Games.CreationEngine.Tests;

/// <summary>Reading back the plugin list a game keeps on disk.</summary>
public class AsteriskMechanismTests(ITestOutputHelper helper)
    : AIsolatedGameTest<AsteriskMechanismTests, CreationEngine.Fallout4.Fallout4>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddCreationEngine()
        .AddUniversalGameLocator<CreationEngine.Fallout4.Fallout4>(new Version("1.10.163"), locationsFileSystem: IsolatedKnownFolders.Create);

    [Fact]
    public async Task ReadGameState_ParsesStarredEntriesAsActive()
    {
        var loadout = await CreateLoadout();
        var file = GameInstallation.Locations.ToAbsolutePath(Game.PluginsFile);
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync("# comment\n*A.esp\nb.esp\r\n\n*C.esl\n");

        var state = await new AsteriskMechanism().ReadGameState(Game, loadout);

        state!.Entries.Should().Equal(("A.esp", true), ("b.esp", false), ("C.esl", true));
    }

    [Fact]
    public async Task ReadGameState_ReadsPluginsTxtAsWindows1252()
    {
        var loadout = await CreateLoadout();
        var file = GameInstallation.Locations.ToAbsolutePath(Game.PluginsFile);
        file.Parent.CreateDirectory();
        await file.WriteAllBytesAsync([(byte)'*', 0xDC, (byte)'n', 0xEF, (byte)'c', 0xF6, (byte)'d', 0xE9, (byte)'.', (byte)'e', (byte)'s', (byte)'p', (byte)'\n']);

        var state = await new AsteriskMechanism().ReadGameState(Game, loadout);

        state!.Entries.Should().Equal(("\u00dcn\u00efc\u00f6d\u00e9.esp", true));
    }

    [Fact]
    public async Task ReadGameState_IsNullWhenTheGameHasNoListYet()
    {
        var loadout = await CreateLoadout();
        GameInstallation.Locations.ToAbsolutePath(Game.PluginsFile).Delete();

        (await new AsteriskMechanism().ReadGameState(Game, loadout)).Should().BeNull();
    }

    [Fact]
    public void TheMechanismOffersThePluginsFileOfTheGame()
    {
        var game = (ICreationEngineGame)Game;

        var files = new AsteriskMechanism().IntrinsicFiles(game, default, ServiceProvider).ToList();

        files.Should().ContainSingle().Which.Path.Should().Be(game.PluginsFile);
        files[0].Should().BeOfType<PluginsFile>();
    }
}

/// <summary>Skyrim LE and Enderal keep the active plugins in plugins.txt and the whole order in loadorder.txt.</summary>
public class TextfileMechanismTests(ITestOutputHelper helper)
    : AIsolatedGameTest<TextfileMechanismTests, CreationEngine.SkyrimLE.SkyrimLE>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddCreationEngine()
        .AddUniversalGameLocator<CreationEngine.SkyrimLE.SkyrimLE>(new Version("1.9.32.0"), locationsFileSystem: IsolatedKnownFolders.Create);

    private AbsolutePath ActiveFile => GameInstallation.Locations.ToAbsolutePath(Game.PluginsFile);
    private AbsolutePath OrderFile => ActiveFile.Parent / "loadorder.txt";

    private async Task Write(AbsolutePath file, string text)
    {
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync(text);
    }

    [Fact]
    public async Task ReadGameState_TakesTheOrderFromLoadOrderTxtAndTheSwitchesFromPluginsTxt()
    {
        var loadout = await CreateLoadout();
        await Write(OrderFile, "# comment\nSkyrim.esm\nB.esp\nA.esp\nC.esp\n");
        await Write(ActiveFile, "Skyrim.esm\nA.esp\n");

        var state = await new TextfileMechanism().ReadGameState(Game, loadout);

        state!.Entries.Should().Equal(("Skyrim.esm", true), ("B.esp", false), ("A.esp", true), ("C.esp", false));
    }

    [Fact]
    public async Task ReadGameState_ReadsPluginsTxtAsWindows1252AndLoadOrderTxtAsUtf8()
    {
        var loadout = await CreateLoadout();
        await Write(OrderFile, "Skyrim.esm\n\u00dcn\u00efc\u00f6d\u00e9.esp\n");
        ActiveFile.Parent.CreateDirectory();
        await ActiveFile.WriteAllBytesAsync([.. "Skyrim.esm\n"u8, 0xDC, (byte)'n', 0xEF, (byte)'c', 0xF6, (byte)'d', 0xE9, (byte)'.', (byte)'e', (byte)'s', (byte)'p', (byte)'\n']);

        var state = await new TextfileMechanism().ReadGameState(Game, loadout);

        state!.Entries.Should().Equal(("Skyrim.esm", true), ("\u00dcn\u00efc\u00f6d\u00e9.esp", true));
    }

    [Fact]
    public async Task ReadGameState_FallsBackToWindows1252ForALoadOrderTxtThatIsNotUtf8()
    {
        var loadout = await CreateLoadout();
        OrderFile.Parent.CreateDirectory();
        await OrderFile.WriteAllBytesAsync([.. "Skyrim.esm\n"u8, 0xDC, (byte)'n', 0xEF, (byte)'c', 0xF6, (byte)'d', 0xE9, (byte)'.', (byte)'e', (byte)'s', (byte)'p', (byte)'\n']);
        await Write(ActiveFile, "Skyrim.esm\n");

        var state = await new TextfileMechanism().ReadGameState(Game, loadout);

        state!.Entries.Should().Equal(("Skyrim.esm", true), ("\u00dcn\u00efc\u00f6d\u00e9.esp", false));
    }

    [Fact]
    public async Task ReadGameState_MatchesNamesIgnoringCase()
    {
        var loadout = await CreateLoadout();
        await Write(OrderFile, "Skyrim.esm\nA.esp\n");
        await Write(ActiveFile, "skyrim.ESM\na.esp\n");

        var state = await new TextfileMechanism().ReadGameState(Game, loadout);

        state!.Entries.Should().Equal(("Skyrim.esm", true), ("A.esp", true));
    }

    [Fact]
    public async Task ReadGameState_FollowsPluginsTxtWhenThereIsNoLoadOrderTxt()
    {
        var loadout = await CreateLoadout();
        await Write(ActiveFile, "# comment\nB.esp\nA.esp\n");

        var state = await new TextfileMechanism().ReadGameState(Game, loadout);

        state!.Entries.Should().Equal(("B.esp", true), ("A.esp", true));
    }

    [Fact]
    public async Task ReadGameState_AppendsActivePluginsLoadOrderTxtLeftOut()
    {
        var loadout = await CreateLoadout();
        await Write(OrderFile, "A.esp\n");
        await Write(ActiveFile, "A.esp\nB.esp\n");

        var state = await new TextfileMechanism().ReadGameState(Game, loadout);

        state!.Entries.Should().Equal(("A.esp", true), ("B.esp", true));
    }

    [Fact]
    public async Task ReadGameState_ListsEverythingAsInactiveWithoutPluginsTxt()
    {
        var loadout = await CreateLoadout();
        await Write(OrderFile, "A.esp\nB.esp\n");

        var state = await new TextfileMechanism().ReadGameState(Game, loadout);

        state!.Entries.Should().Equal(("A.esp", false), ("B.esp", false));
    }

    [Fact]
    public async Task ReadGameState_IsNullWhenTheGameHasNeitherFile()
    {
        var loadout = await CreateLoadout();
        ActiveFile.Delete();
        OrderFile.Delete();

        (await new TextfileMechanism().ReadGameState(Game, loadout)).Should().BeNull();
    }

    [Fact]
    public void TheMechanismOffersBothFilesBesideEachOther()
    {
        var game = (ICreationEngineGame)Game;

        var files = new TextfileMechanism().IntrinsicFiles(game, default, ServiceProvider).ToList();

        files.Select(f => f.Path).Should().Equal(game.PluginsFile, game.PluginsFile.Parent / "loadorder.txt");
        files.Should().AllBeOfType<PluginsFile>();
    }

    [Fact]
    public void ItOrdersByLoadOrderTxtAndNotByTimestamp() => new TextfileMechanism().OrdersByTimestamp.Should().BeFalse();
}
