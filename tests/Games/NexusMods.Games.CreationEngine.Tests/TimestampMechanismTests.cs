using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Games.CreationEngine.PluginLists;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.StandardGameLocators.TestHelpers;

namespace NexusMods.Games.CreationEngine.Tests;

/// <summary>Oblivion keeps the active plugins in Plugins.txt and takes their order from the file times in Data.</summary>
public class OblivionTimestampMechanismTests(ITestOutputHelper helper)
    : AIsolatedGameTest<OblivionTimestampMechanismTests, CreationEngine.Oblivion.Oblivion>(helper)
{
    private static readonly DateTime T0 = new(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddCreationEngine()
        .AddUniversalGameLocator<CreationEngine.Oblivion.Oblivion>(new Version("1.2.0.416"), locationsFileSystem: IsolatedKnownFolders.Create);

    private TimestampMechanism Mechanism => (TimestampMechanism)((ICreationEngineGame)Game).PluginList(GameInstallation);

    private AbsolutePath DataFile(string name) => GameInstallation.Locations.ToAbsolutePath(((ICreationEngineGame)Game).DataPath / name);

    private async Task Plugin(string name, int minutes)
    {
        var file = DataFile(name);
        file.Parent.CreateDirectory();
        await file.WriteAllBytesAsync([0]);
        File.SetLastWriteTimeUtc(file.ToString(), T0.AddMinutes(minutes));
    }

    private async Task WritePluginsTxt(string text, GamePath? path = null)
    {
        var file = GameInstallation.Locations.ToAbsolutePath(path ?? Game.PluginsFile);
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync(text);
    }

    [Fact]
    public async Task ReadGameState_OrdersTheDataPluginsByTimeAndTakesTheSwitchesFromPluginsTxt()
    {
        var loadout = await CreateLoadout();
        await Plugin("Late.esp", 30);
        await Plugin("Oblivion.esm", 0);
        await Plugin("Middle.esp", 10);
        await WritePluginsTxt("# comment\nOblivion.esm\nlate.ESP\n");

        var state = await Mechanism.ReadGameState(Game, loadout);

        state!.Entries.Should().Equal(("Oblivion.esm", true), ("Middle.esp", false), ("Late.esp", true));
    }

    [Fact]
    public async Task ReadGameState_BreaksTimeTiesByName()
    {
        var loadout = await CreateLoadout();
        await Plugin("B.esp", 5);
        await Plugin("A.esp", 5);
        await WritePluginsTxt("A.esp\n");

        var state = await Mechanism.ReadGameState(Game, loadout);

        state!.Entries.Select(e => e.Name).Should().Equal("A.esp", "B.esp");
    }

    [Fact]
    public async Task ReadGameState_IsNullWhenTheGameHasNoPluginsFile()
    {
        var loadout = await CreateLoadout();
        await Plugin("Oblivion.esm", 0);
        // Creating the loadout already wrote one.
        GameInstallation.Locations.ToAbsolutePath(Game.PluginsFile).Delete();

        (await Mechanism.ReadGameState(Game, loadout)).Should().BeNull();
    }

    [Fact]
    public async Task ReadGameState_LooksForPluginsTxtInTheGameFolderWhenTheIniSaysSo()
    {
        var loadout = await CreateLoadout();
        await GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "Oblivion.ini"))
            .WriteAllTextAsync("[General]\nbUseMyGamesDirectory=0\n");
        await Plugin("A.esp", 1);
        await WritePluginsTxt("A.esp\n", new GamePath(LocationId.Game, "Plugins.txt"));

        var state = await Mechanism.ReadGameState(Game, loadout);

        state!.Entries.Should().Equal(("A.esp", true));
    }

    [Fact]
    public async Task TheMechanismOffersOnePluginsFileAtThePathTheIniPicks()
    {
        var loadout = await CreateLoadout();
        var game = (ICreationEngineGame)Game;
        Mechanism.OrdersByTimestamp.Should().BeTrue();
        Mechanism.IntrinsicFiles(game, loadout, ServiceProvider).Select(f => f.Path).Should().Equal(new GamePath(LocationId.AppData, "Plugins.txt"));

        await GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "Oblivion.ini"))
            .WriteAllTextAsync("[General]\nbUseMyGamesDirectory=0\n");

        Mechanism.IntrinsicFiles(game, loadout, ServiceProvider).Select(f => f.Path).Should().Equal(new GamePath(LocationId.Game, "Plugins.txt"));
    }
}
