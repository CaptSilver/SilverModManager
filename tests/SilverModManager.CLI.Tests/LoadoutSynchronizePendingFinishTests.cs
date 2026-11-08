using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.FromSoft;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.TestFramework;
using SilverModManager.Platform.Proton;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.SingleProcess;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>loadout synchronize</c> runs the finish a closed launch left pending before it applies, and says so on stderr
/// so a script reading stdout sees only what it asked for.
/// </summary>
[Collection("ConsoleOutput")]
public class LoadoutSynchronizePendingFinishTests(ITestOutputHelper helper) : AIsolatedGameTest<LoadoutSynchronizePendingFinishTests, EldenRingGame>(helper)
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
            .AddHeadlessLaunch()
            .ReplaceDesktopNotifier();
        return services;
    }

    [Fact]
    public async Task LoadoutSynchronize_WithAPendingFinish_PrintsTheFinishLineToStderr()
    {
        var loadout = await CreateLoadout();
        // A marker naming a loadout that no longer exists: the finisher clears it and has a line for the person.
        await ServiceProvider.GetRequiredService<ILaunchFinishMarkers>()
            .Write(GameInstallation, LoadoutId.From(0xDEAD), Guid.NewGuid(), []);

        var renderer = new LoggingRenderer();
        var stderr = new StringWriter();
        var original = Console.Error;
        Console.SetError(stderr);
        int exitCode;
        try
        {
            exitCode = await ServiceProvider.GetRequiredService<CommandLineConfigurator>()
                .RunAsync(["loadout", "synchronize", "-l", loadout.LoadoutId.ToString()], renderer, CancellationToken.None);
        }
        finally
        {
            Console.SetError(original);
        }

        var stdout = string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Template + string.Concat(t.Arguments)));
        exitCode.Should().Be(0, stdout);
        var line = $"{GameInstallation.Game.DisplayName} closed before its changes were taken back into its loadout, and that loadout has since been deleted. The files are still in the game folder.";
        stderr.ToString().Should().Contain(line);
        stdout.Should().NotContain("closed before its changes");
    }
}
