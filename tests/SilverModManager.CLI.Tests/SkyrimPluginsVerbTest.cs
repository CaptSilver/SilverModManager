using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.DataModel.Undo;
using SilverModManager.Games.Bethesda.Loot.Masterlists;
using SilverModManager.Games.CreationEngine;
using SilverModManager.Games.CreationEngine.TestHelpers;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.SingleProcess;
using SilverModManager.StandardGameLocators.TestHelpers;
using NexusMods.Paths;
using SkyrimSEGame = SilverModManager.Games.CreationEngine.SkyrimSE.SkyrimSE;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// Runs verbs against a Skyrim SE loadout whose plugins are A, B and C after Skyrim.esm, with a masterlist the test
/// sets instead of the bundled one.
/// </summary>
public abstract class SkyrimPluginsVerbTest<TSelf>(ITestOutputHelper helper) : AIsolatedGameTest<TSelf, SkyrimSEGame>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services);
        services
            .AddCreationEngine()
            .AddUniversalGameLocator<SkyrimSEGame>(new Version("1.6.1170"), locationsFileSystem: IsolatedKnownFolders.Create)
            .AddSingleton<CommandLineConfigurator>()
            .AddDefaultParsers()
            .AddCLI()
            .ReplaceDesktopNotifier();
        // The fixture goes in last, so it replaces the masterlist store the game registers.
        return FixtureMasterlistStore.AddFixtureMasterlist(services);
    }

    protected FixtureMasterlistStore Masterlists => ServiceProvider.GetRequiredService<FixtureMasterlistStore>();

    protected async Task<(int ExitCode, string Output)> Run(params string[] args)
    {
        var renderer = new LoggingRenderer();
        var exitCode = await ServiceProvider.GetRequiredService<CommandLineConfigurator>().RunAsync(args, renderer, CancellationToken.None);
        return (exitCode, string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Template + string.Concat(t.Arguments))));
    }

    protected int LatestRevisionNumber(LoadoutId loadout) =>
        ServiceProvider.GetRequiredService<UndoService>().RevisionsFor(loadout.Value).Count - 1;

    /// <summary>A loadout with Skyrim.esm, then A, B and C in that order.</summary>
    protected async Task<LoadoutId> LoadoutWithABC()
    {
        // Created before the plugins are stored: creating a loadout runs the garbage collector.
        var loadout = await CreateLoadout();
        await SkyrimBaseline.SeedSkyrimEsm(ServiceProvider, loadout);
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, "Mods");
        foreach (var plugin in new[] { "A.esp", "B.esp", "C.esp" })
            _ = AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, $"Data/{plugin}"));
        await tx.Commit();
        return loadout.LoadoutId;
    }

    protected async Task<string[]> PluginOrder(LoadoutId loadout)
    {
        var (exit, output) = await Run("loadout", "plugins", "list", "-l", loadout.ToString(), "--json");
        if (exit != 0) throw new InvalidOperationException(output);
        return JsonNode.Parse(output)!.AsArray().Select(row => (string)row!["name"]!).ToArray();
    }
}
