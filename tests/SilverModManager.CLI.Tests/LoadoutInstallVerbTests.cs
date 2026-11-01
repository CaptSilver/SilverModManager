using System.IO.Compression;
using FluentAssertions;
using NexusMods.Paths;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.FromSoft;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.SingleProcess;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>silvermod loadout install</c>: the file it adds to the library is recorded for the loadout's game.
/// </summary>
[Collection("ConsoleOutput")]
public class LoadoutInstallVerbTests(ITestOutputHelper helper) : AIsolatedGameTest<LoadoutInstallVerbTests, EldenRingGame>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services)
            .AddFromSoftGames()
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

    private async Task<(int ExitCode, string Output)> Run(params string[] args)
    {
        var renderer = new LoggingRenderer();
        var exitCode = await ServiceProvider.GetRequiredService<CommandLineConfigurator>().RunAsync(args, renderer, CancellationToken.None);
        return (exitCode, string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Template)));
    }

    [Fact]
    public async Task TheInstalledFile_IsRecordedForTheLoadoutsGame()
    {
        await GenerateGameFiles();
        var loadout = await ServiceProvider.GetRequiredService<ILoadoutManager>()
            .CreateLoadout(GameInstallation, "install target", BackupOverflowChoice.Refuse);
        var path = Path.Combine(Directory.CreateTempSubdirectory("verb-install-").FullName, "verb-mod.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("verb-mod.txt").Open());
            writer.Write("verb-mod");
        }

        var (_, output) = await Run("loadout", "install", "-l", loadout.LoadoutId.ToString(), "-f", path, "-n", "Verb mod");

        // The library add happens before the install, so the recorded game does not depend on the install succeeding.
        LibraryDestinations.ItemsFor(Connection.Db, EldenRingGame.GameId).Should().ContainSingle(item => item.Name == "verb-mod.zip", output);
        LibraryDestinations.Unassigned(Connection.Db).Should().NotContain(item => item.Name == "verb-mod.zip");
    }
}
