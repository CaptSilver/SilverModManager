using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Backend;
using NexusMods.Games.Generic;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.StandardGameLocators.TestHelpers;
using Xunit;

namespace NexusMods.Games.RedEngine.Tests;

/// <summary>
/// Only Steam has a Proton compatibility data provider, but a GOG copy living in a Wine prefix is just as
/// able to run redmod, so the deploy must not depend on the store.
/// </summary>
public class RedModDeployToolGogInWineTests : AIsolatedGameTest<RedModDeployToolGogInWineTests, Cyberpunk2077Game>
{
    public RedModDeployToolGogInWineTests(ITestOutputHelper helper) : base(helper) { }

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddOSInterop()
            .AddRuntimeDependencies()
            .AddGenericGameSupport()
            .AddFakeToolRunner()
            .AddSingleton<IGameLocator>(provider => new UniversalStubbedGameLocator<Cyberpunk2077Game>(
                provider,
                provider.GetRequiredService<IFileSystem>(),
                provider.GetRequiredService<TemporaryFileManager>(),
                stores: [GameStore.GOG]
            )
            {
                LocatorIds = [LocatorId.From("3976631895")],
            })
            .AddRedEngineGames();
    }

    [Fact]
    public async Task Execute_GogInstallInWinePrefix_RunsRedMod()
    {
        var loadout = await CreateLoadout();
        loadout.Installation.Store.Should().Be(GameStore.GOG);
        loadout.InstallationInstance.LocatorResult.LinuxCompatabilityDataProvider.Should().BeNull();

        var tool = ServiceProvider.GetServices<ITool>().OfType<RedModDeployTool>().Single();
        await tool.Execute(loadout, CancellationToken.None);

        ServiceProvider.GetRequiredService<FakeToolRunner>().Calls.Should().ContainSingle()
            .Which.Args.Should().Equal("deploy", "-force", "-modlist=C:\\modlist.txt");
    }
}
