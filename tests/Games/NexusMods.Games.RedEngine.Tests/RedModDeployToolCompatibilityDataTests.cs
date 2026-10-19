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
/// RedMod only runs through Proton, so a Steam install whose locator result carries no
/// compatibility data has to fail loudly rather than skip the deploy.
/// </summary>
public class RedModDeployToolCompatibilityDataTests : AIsolatedGameTest<RedModDeployToolCompatibilityDataTests, Cyberpunk2077Game>
{
    public RedModDeployToolCompatibilityDataTests(ITestOutputHelper helper) : base(helper) { }

    // The stubbed locator never attaches a LinuxCompatabilityDataProvider, which is the
    // situation under test; the Steam store is what gets Execute past the store check.
    // Steam locator IDs are numeric manifest IDs, and the sync tree query casts them to integers.
    // The stubbed file hasher only knows this one for Steam.
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddOSInterop()
            .AddRuntimeDependencies()
            .AddGenericGameSupport()
            .AddSingleton<IGameLocator>(provider => new UniversalStubbedGameLocator<Cyberpunk2077Game>(
                provider,
                provider.GetRequiredService<IFileSystem>(),
                provider.GetRequiredService<TemporaryFileManager>(),
                stores: [GameStore.Steam]
            )
            {
                LocatorIds = [LocatorId.From("3976631895")],
            })
            .AddRedEngineGames();
    }

    [Fact]
    public async Task Execute_SteamInstallWithoutCompatibilityData_ThrowsNamingTheGame()
    {
        var loadout = await CreateLoadout();
        loadout.InstallationInstance.LocatorResult.LinuxCompatabilityDataProvider.Should().BeNull();

        var tool = ServiceProvider.GetServices<ITool>().OfType<RedModDeployTool>().Single();

        var act = async () => await tool.Execute(loadout, CancellationToken.None);

        var exception = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        exception.Message.Should().Contain(Cyberpunk2077Game.DisplayName);
        exception.Message.Should().Contain("Proton");
        exception.Message.Should().Contain("Wine");
    }
}
