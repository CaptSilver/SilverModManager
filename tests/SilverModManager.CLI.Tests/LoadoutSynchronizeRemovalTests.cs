using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.FromSoft;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.SingleProcess;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>loadout synchronize</c> over a loadout whose only mod is switched off: the apply would remove every file the
/// loadout deployed, so the command refuses unless <c>--confirm-removal</c> says to go ahead. It never opens the
/// removal dialog of a window it may be forwarded to.
/// </summary>
[Collection("ConsoleOutput")]
public class LoadoutSynchronizeRemovalTests(ITestOutputHelper helper) : AIsolatedGameTest<LoadoutSynchronizeRemovalTests, EldenRingGame>(helper)
{
    private readonly ThrowingPrompt _prompt = new();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services)
            .AddFromSoftGames()
            .AddSingleton<IMassRemovalPrompt>(_prompt)
            .AddSingleton<IGameLocator>(provider => new UniversalStubbedGameLocator<EldenRingGame>(
                provider,
                provider.GetRequiredService<IFileSystem>(),
                provider.GetRequiredService<TemporaryFileManager>(),
                stores: [GameStore.Steam])
            {
                LocatorIds = [LocatorId.From("3976631895")],
            })
            .AddSingleton<CommandLineConfigurator>()
            .AddDefaultParsers()
            .AddCLI()
            .ReplaceDesktopNotifier();
        return services;
    }

    private sealed class ThrowingPrompt : IMassRemovalPrompt
    {
        public int Asked;

        public ValueTask<bool> Confirm(GameInstallation installation, MassRemoval removal, CancellationToken cancellationToken)
        {
            Asked++;
            throw new InvalidOperationException("a verb must not open the prompt");
        }
    }

    private async Task<(int ExitCode, string Output)> Run(params string[] args)
    {
        var renderer = new LoggingRenderer();
        var exitCode = await ServiceProvider.GetRequiredService<CommandLineConfigurator>().RunAsync(args, renderer, CancellationToken.None);
        return (exitCode, string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Template + string.Concat(t.Arguments))));
    }

    private async Task<Loadout.ReadOnly> LoadoutWithASwitchedOffMod()
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, Enumerable.Range(0, 12).Select(i => (RelativePath)$"mod/cli_mod/f{i}.txt"), loadout.LoadoutId, "Cli Mod");
            await tx.Commit();
        }

        await SynchronizerService.Synchronize(loadout.LoadoutId);
        var mod = LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemGroup().Single(g => g.AsLoadoutItem().Name == "Cli Mod");
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(mod.Id, LoadoutItem.Disabled, Null.Instance);
            await tx.Commit();
        }
        return loadout;
    }

    private int DeployedFiles
    {
        get
        {
            var folder = GameInstallation.LocatorResult.Path / "mod" / "cli_mod";
            return folder.DirectoryExists() ? folder.EnumerateFiles(recursive: true).Count() : 0;
        }
    }

    [Fact]
    public async Task WithoutTheFlag_RefusesNamingTheModAndTheFlagAndNeverAsks()
    {
        var loadout = await LoadoutWithASwitchedOffMod();
        DeployedFiles.Should().Be(12);

        var (exit, output) = await Run("loadout", "synchronize", "-l", loadout.LoadoutId.ToString());

        exit.Should().NotBe(0);
        output.Should().Contain("Cli Mod is switched off").And.Contain("--confirm-removal");
        _prompt.Asked.Should().Be(0);
        DeployedFiles.Should().Be(12);
    }

    [Fact]
    public async Task WithTheFlag_RemovesTheFilesWithoutAsking()
    {
        var loadout = await LoadoutWithASwitchedOffMod();

        var (exit, output) = await Run("loadout", "synchronize", "-l", loadout.LoadoutId.ToString(), "--confirm-removal");

        exit.Should().Be(0, output);
        _prompt.Asked.Should().Be(0);
        DeployedFiles.Should().Be(0);
    }
}
