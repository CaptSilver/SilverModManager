using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Games.CreationEngine.PluginLists;
using NexusMods.Games.TestFramework;
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

public class PlainMechanismTests(ITestOutputHelper helper)
    : AIsolatedGameTest<PlainMechanismTests, CreationEngine.SkyrimLE.SkyrimLE>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddCreationEngine()
        .AddUniversalGameLocator<CreationEngine.SkyrimLE.SkyrimLE>(new Version("1.9.32.0"), locationsFileSystem: IsolatedKnownFolders.Create);

    [Fact]
    public async Task ReadGameState_ListsEveryEntryAsActive()
    {
        var loadout = await CreateLoadout();
        var file = GameInstallation.Locations.ToAbsolutePath(Game.PluginsFile);
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync("# comment\nA.esp\nB.esp\n");

        var state = await new PlainMechanism().ReadGameState(Game, loadout);

        state!.Entries.Should().Equal(("A.esp", true), ("B.esp", true));
    }
}
