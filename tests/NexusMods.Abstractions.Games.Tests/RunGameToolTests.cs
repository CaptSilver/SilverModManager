using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Sdk.Loadouts;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.StandardGameLocators.TestHelpers;
using NexusMods.StandardGameLocators.TestHelpers.StubbedGames;
using NSubstitute;

namespace NexusMods.Abstractions.Games.Tests;

public class RunGameToolTests(ITestOutputHelper helper) : AIsolatedGameTest<RunGameToolTests, StubbedGame>(helper)
{
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Uri> _openedUris = [];
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        // Registered after the base graph so the fake wins: nothing here may reach the real desktop.
        // Opening the URI cancels the run, which stops it before it starts polling the process table for Steam.
        var osInterop = Substitute.For<IOSInterop>();
        osInterop.When(x => x.OpenUri(Arg.Any<Uri>())).Do(call =>
        {
            _openedUris.Add(call.Arg<Uri>());
            _cts.Cancel();
        });

        return base.AddServices(services)
            .AddGame<StubbedGame>()
            // Steam locator ids are numeric build ids, and the stubbed file hasher only knows this one for Steam.
            // The store identifier is what ends up in the steam:// URL.
            .AddSingleton<IGameLocator>(s => new UniversalStubbedGameLocator<StubbedGame>(
                s,
                s.GetRequiredService<IFileSystem>(),
                s.GetRequiredService<TemporaryFileManager>(),
                stores: [GameStore.Steam])
            {
                LocatorIds = [LocatorId.From("3976631895")],
            })
            .AddSingleton(osInterop)
            .AddSingleton(_processRunner);
    }

    private FakeProcessTracker Tracker => ServiceProvider.GetRequiredService<FakeProcessTracker>();

    private sealed class ArgsTool(IServiceProvider provider, StubbedGame game, string[] extraArgs) : RunGameTool<StubbedGame>(provider, game)
    {
        public override async ValueTask<LaunchSpec> GetLaunchSpec(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
        {
            var baseSpec = await base.GetLaunchSpec(loadout, cancellationToken);
            return baseSpec with { ExtraArgs = extraArgs };
        }
    }

    [Fact]
    public async Task BaseSpec_IsThePrimaryFileWithNoArguments()
    {
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, Guid.NewGuid().ToString());
        IRunGameTool tool = new RunGameTool<StubbedGame>(ServiceProvider, Game);

        var spec = await tool.GetLaunchSpec(loadout, CancellationToken.None);

        spec.Exe.Should().Be(GameInstallation.Locations.ToAbsolutePath(Game.GetPrimaryFile(GameInstallation)));
        spec.ExtraArgs.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtraArgs_ReachTheSteamUrlAfterTheCallersArguments()
    {
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, Guid.NewGuid().ToString());
        var tool = new ArgsTool(ServiceProvider, Game, ["-extra"]);

        await tool.Execute(loadout, _cts.Token, ["-caller"]);

        var appId = GameInstallation.LocatorResult.StoreIdentifier;
        _openedUris.Should().ContainSingle()
            .Which.Should().Be(new Uri($"steam://run/{appId}//-caller -extra/"));
    }

    [Fact]
    public async Task ExtraArgs_AloneStillReachTheSteamUrl()
    {
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, Guid.NewGuid().ToString());
        var tool = new ArgsTool(ServiceProvider, Game, ["-extra"]);

        await tool.Execute(loadout, _cts.Token, null);

        var appId = GameInstallation.LocatorResult.StoreIdentifier;
        _openedUris.Should().ContainSingle()
            .Which.Should().Be(new Uri($"steam://run/{appId}//-extra/"));
    }

    private sealed class LauncherTool(IServiceProvider provider, StubbedGame game) : RunGameTool<StubbedGame>(provider, game)
    {
        public override async ValueTask<LaunchSpec> GetLaunchSpec(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
        {
            var baseSpec = await base.GetLaunchSpec(loadout, cancellationToken);
            return baseSpec with { Exe = baseSpec.Exe.Parent.Combine("launcher.exe") };
        }
    }

    [Fact]
    public async Task Execute_WaitsForGameProcessesTheTrackerFindsAfterTheLaunch()
    {
        // Not the primary file, so the tool starts the launcher itself instead of going through the store.
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, Guid.NewGuid().ToString());
        var tool = new LauncherTool(ServiceProvider, Game);
        var gameExe = GameInstallation.Locations.ToAbsolutePath(Game.GetPrimaryFile(GameInstallation)).FileName;

        // The game is already running once; the launcher starts a second copy that has to exit too.
        var launched = false;
        var polls = 0;
        Tracker.ByName = name =>
        {
            if (name != gameExe) return [];
            if (!launched) return [new TrackedProcess(10, name)];
            polls++;
            return polls == 1 ? [new TrackedProcess(10, name), new TrackedProcess(11, name)] : [new TrackedProcess(10, name)];
        };
        _processRunner.RunAsync(Arg.Any<CliWrap.Command>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            launched = true;
            return Task.FromResult(new CliWrap.CommandResult(0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        });

        await tool.Execute(loadout, CancellationToken.None, []);

        polls.Should().Be(2, "it polls again while the new process is alive and stops once only the old one is left");
        Tracker.NameQueries.Should().Contain(["launcher.exe", gameExe]);
    }
}
