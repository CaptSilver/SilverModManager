using FluentAssertions;
using NexusMods.Paths;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Games.FromSoft;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.SingleProcess;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>silvermod loadout create</c> over a stubbed game the locator finds but nobody has managed yet.
/// </summary>
[Collection("ConsoleOutput")]
public class LoadoutCreateVerbTests(ITestOutputHelper helper) : AIsolatedGameTest<LoadoutCreateVerbTests, EldenRingGame>(helper)
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
    public async Task CreatesALoadoutForALocatedGameAndPrintsItsId()
    {
        await GenerateGameFiles();

        var (exit, output) = await Run("loadout", "create", "-g", EldenRingGame.GameId.ToString(), "-n", "Created by the verb");

        exit.Should().Be(0, output);
        var loadout = Loadout.All(Connection.Db).Where(l => l.IsVisible()).Should().ContainSingle().Which;
        loadout.Name.Should().Be("Created by the verb");
        output.Should().Contain(loadout.LoadoutId.ToString());
    }

    [Fact]
    public async Task AGameThatIsNotLocatedSaysSoAndCreatesNothing()
    {
        var (exit, output) = await Run("loadout", "create", "-g", "Not.Installed");

        exit.Should().NotBe(0);
        output.Should().Contain("Not.Installed").And.Contain("games list");
        Loadout.All(Connection.Db).Where(l => l.IsVisible()).Should().BeEmpty();
    }
}
