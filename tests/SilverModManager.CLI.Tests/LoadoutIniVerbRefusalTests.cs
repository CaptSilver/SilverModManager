using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Ini;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.CLI.Tests;

/// <summary>The INI verbs refuse a loadout of a game that does not keep INI copies.</summary>
[Collection("ConsoleOutput")]
public class LoadoutIniVerbRefusalTests(ITestOutputHelper helper) : EldenRingVerbTest<LoadoutIniVerbRefusalTests>(helper)
{
    private static readonly GameId NoIniGameId = GameId.From("Test.NoIniFiles");

    /// <summary>A game that implements <see cref="IGameIniFiles"/> but lists no file.</summary>
    private sealed class NoIniFilesGame() : StubGameData("No Ini Files", NoIniGameId), IGameIniFiles
    {
        public IniSet IniFiles { get; } = new();
        public GamePath IniPath(string fileName) => throw new NotSupportedException();
        public GamePath? ManagedKeysFile => null;
        public IReadOnlyList<IniKeyEdit> ManagedKeys(Loadout.ReadOnly loadout) => [];
    }

    protected override IServiceCollection AddServices(IServiceCollection services) =>
        base.AddServices(services).AddSingleton<IGameData, NoIniFilesGame>();

    [Fact]
    public async Task ListRefusesALoadoutOfAGameWithoutInis()
    {
        var loadout = await CreateLoadout();

        var (exit, output) = await Run("loadout", "ini", "list", "-l", loadout.LoadoutId.ToString());

        exit.Should().NotBe(0);
        output.Should().Contain("does not keep that game's INI files");
    }

    [Fact]
    public async Task ListRefusesAGameThatOwnsNoInis()
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(loadout.Installation.Id, GameInstallMetadata.Game, NoIniGameId);
            await tx.Commit();
        }

        var (listExit, listOutput) = await Run("loadout", "ini", "list", "-l", loadout.LoadoutId.ToString());
        var (showExit, showOutput) = await Run("loadout", "ini", "show", "-l", loadout.LoadoutId.ToString(), "-f", "Any.ini");

        listExit.Should().NotBe(0);
        listOutput.Should().Contain("is a No Ini Files loadout. The app does not keep that game's INI files");
        showExit.Should().NotBe(0);
        showOutput.Should().Contain("The app does not keep that game's INI files");
    }
}
