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
            .AddSingleton(Substitute.For<IProcessRunner>());
    }

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
}
