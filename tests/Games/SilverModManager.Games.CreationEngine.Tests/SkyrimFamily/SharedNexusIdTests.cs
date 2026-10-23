using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.TestFramework;
using NexusMods.Sdk.Games;
using NexusMods.StandardGameLocators.TestHelpers;

namespace NexusMods.Games.CreationEngine.Tests.SkyrimFamily;

/// <summary>Skyrim SE and Skyrim VR are two games on one Nexus id: each install keeps its own game id and folder.</summary>
public class SharedNexusIdTests(ITestOutputHelper helper)
    : AIsolatedGameTest<SharedNexusIdTests, CreationEngine.SkyrimSE.SkyrimSE>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddCreationEngine()
        .AddUniversalGameLocator<CreationEngine.SkyrimSE.SkyrimSE>(new Version("1.6.1170"), locationsFileSystem: IsolatedKnownFolders.Create)
        .AddUniversalGameLocator<CreationEngine.SkyrimVR.SkyrimVR>(new Version("1.4.15"), locationsFileSystem: IsolatedKnownFolders.Create);

    [Fact]
    public async Task EachGameKeepsItsOwnInstallationAndLoadout()
    {
        var installs = GameRegistry.LocateGameInstallations()
            .Where(i => i.Game is CreationEngine.SkyrimSE.SkyrimSE or CreationEngine.SkyrimVR.SkyrimVR)
            .ToArray();
        installs.Should().HaveCount(2);
        CreationEngine.SkyrimSE.SkyrimSE.NexusModsGameId.Value.Value.Should().Be(1704);
        CreationEngine.SkyrimVR.SkyrimVR.NexusModsGameId.Value.Value.Should().Be(1704);

        var metadata = new List<GameInstallMetadata.ReadOnly>();
        foreach (var install in installs)
            metadata.Add(await LoadoutManager.ManageInstallation(install));
        metadata.Select(m => m.Id).Should().OnlyHaveUniqueItems();
        metadata.Select(m => m.Game.Value).Should().BeEquivalentTo([CreationEngine.SkyrimSE.SkyrimSE.GameId, CreationEngine.SkyrimVR.SkyrimVR.GameId]);

        foreach (var install in installs)
        {
            var loadout = await LoadoutManager.CreateLoadout(install, Guid.NewGuid().ToString());

            loadout.InstallationInstance.Game.GameId.Should().Be(install.Game.GameId);
            GameRegistry.TryGetGameInstallation(loadout, out var found).Should().BeTrue();
            found!.LocatorResult.Path.Should().Be(install.LocatorResult.Path);
            found.Game.GameId.Should().Be(install.Game.GameId);
        }
    }
}
