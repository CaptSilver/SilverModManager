using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.TestFramework;
using NexusMods.Sdk.Games;
using NexusMods.StandardGameLocators.TestHelpers;

namespace NexusMods.Games.CreationEngine.Tests.Fallout4VR;

/// <summary>Fallout 4 and Fallout 4 VR are two games on one Nexus id: each install keeps its own game id and folder.</summary>
public class Fallout4SharedNexusIdTests(ITestOutputHelper helper)
    : AIsolatedGameTest<Fallout4SharedNexusIdTests, CreationEngine.Fallout4.Fallout4>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddCreationEngine()
        .AddUniversalGameLocator<CreationEngine.Fallout4.Fallout4>(new Version("1.10.163"), locationsFileSystem: IsolatedKnownFolders.Create)
        .AddUniversalGameLocator<CreationEngine.Fallout4VR.Fallout4VR>(new Version("1.2.72"), locationsFileSystem: IsolatedKnownFolders.Create);

    [Fact]
    public async Task EachGameKeepsItsOwnInstallationAndLoadout()
    {
        var installs = GameRegistry.LocateGameInstallations()
            .Where(i => i.Game is CreationEngine.Fallout4.Fallout4 or CreationEngine.Fallout4VR.Fallout4VR)
            .ToArray();
        installs.Should().HaveCount(2);
        CreationEngine.Fallout4.Fallout4.NexusModsGameId.Value.Value.Should().Be(1151);
        CreationEngine.Fallout4VR.Fallout4VR.NexusModsGameId.Value.Value.Should().Be(1151);

        var metadata = new List<GameInstallMetadata.ReadOnly>();
        foreach (var install in installs)
            metadata.Add(await LoadoutManager.ManageInstallation(install));
        metadata.Select(m => m.Id).Should().OnlyHaveUniqueItems();
        metadata.Select(m => m.Game.Value).Should().BeEquivalentTo([CreationEngine.Fallout4.Fallout4.GameId, CreationEngine.Fallout4VR.Fallout4VR.GameId]);

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
