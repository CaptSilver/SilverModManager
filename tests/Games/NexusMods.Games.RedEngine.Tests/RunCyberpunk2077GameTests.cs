using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Backend;
using NexusMods.Games.Generic;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.StandardGameLocators.TestHelpers;
using NSubstitute;
using Xunit;

namespace NexusMods.Games.RedEngine.Tests;

public class RunCyberpunk2077GameTests(ITestOutputHelper helper) : AIsolatedGameTest<RunCyberpunk2077GameTests, Cyberpunk2077Game>(helper)
{
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Uri> _openedUris = [];

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        // Opening the URI cancels the run before it starts polling the process table for Steam,
        // and nothing here may reach the real desktop.
        var osInterop = Substitute.For<IOSInterop>();
        osInterop.When(x => x.OpenUri(Arg.Any<Uri>())).Do(call =>
        {
            _openedUris.Add(call.Arg<Uri>());
            _cts.Cancel();
        });

        // Steam locator ids are numeric build ids, and the stubbed file hasher only knows this one for Steam.
        return base.AddServices(services)
            .AddRuntimeDependencies()
            .AddGenericGameSupport()
            .AddFakeToolRunner()
            .AddSingleton<IGameLocator>(provider => new UniversalStubbedGameLocator<Cyberpunk2077Game>(
                provider,
                provider.GetRequiredService<IFileSystem>(),
                provider.GetRequiredService<TemporaryFileManager>(),
                stores: [GameStore.Steam])
            {
                LocatorIds = [LocatorId.From("3976631895")],
            })
            .AddRedEngineGames()
            .AddSingleton(osInterop)
            .AddSingleton(Substitute.For<IProcessRunner>());
    }

    private RunCyberpunk2077Game Tool => ServiceProvider.GetServices<ITool>().OfType<RunCyberpunk2077Game>().Single();

    [Fact]
    public async Task LaunchSpec_IsThePrimaryExecutableWithModdedFlag()
    {
        var loadout = await CreateLoadout();

        var spec = await Tool.GetLaunchSpec(loadout, CancellationToken.None);

        spec.Exe.Should().Be(GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "bin/x64/Cyberpunk2077.exe")));
        spec.ExtraArgs.Should().Equal("-modded");
    }

    [Fact]
    public async Task Execute_OpensTheSteamUrlWithoutModded()
    {
        var loadout = await CreateLoadout();

        await Tool.Execute(loadout, _cts.Token, null);

        var appId = GameInstallation.LocatorResult.StoreIdentifier;
        _openedUris.Should().ContainSingle()
            .Which.Should().Be(new Uri($"steam://run/{appId}"));
    }

    [Fact]
    public async Task Execute_RecordsModdedInTheLaunchSessionInsteadOfTheUrl()
    {
        var loadout = await CreateLoadout();

        await Tool.Execute(loadout, _cts.Token, ["-caller"]);

        var appId = GameInstallation.LocatorResult.StoreIdentifier;
        _openedUris.Should().ContainSingle()
            .Which.Should().Be(new Uri($"steam://run/{appId}"));
        var spec = ServiceProvider.GetRequiredService<FakeGameLaunchSession>().BegunWith.Should().ContainSingle().Which;
        spec!.ExtraArgs.Should().Equal("-modded");
    }
}
