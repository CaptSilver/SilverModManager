using FluentAssertions;
using GameFinder.Wine;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.RoundTrip;

/// <summary>
/// Applies an installed mod to a stubbed game and takes it back off again, on the default deployment backend
/// and on copy. AppData and My Games resolve inside a throwaway Wine prefix on the real file system, so the
/// synchronizer can actually write there. The abstract members are literals of the title under test; they are
/// read while the service graph is built, so they must not depend on instance state.
/// </summary>
public abstract class ABethesdaRoundTripTest<TTest, TGame>(ITestOutputHelper helper) : AIsolatedGameTest<TTest, TGame>(helper)
    where TTest : class
    where TGame : class, IGame
{
    private const string VanillaFile = "vanilla.txt";

    protected abstract Version GameVersion { get; }
    protected abstract string PrimaryExe { get; }
    protected abstract GamePath DataPath { get; }

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddCreationEngine()
            .AddUniversalGameLocator<TGame>(GameVersion, new Dictionary<RelativePath, byte[]>
            {
                { PrimaryExe, "game executable"u8.ToArray() },
            }, locationsFileSystem: IsolatedKnownFolders.Create);
    }

    // The stub locator does not create folders, so the vanilla file goes in here. It is deliberately not a
    // plugin: stub bytes under a .esm name would make the plugins file refuse to write.
    protected override async Task GenerateGameFiles()
    {
        var vanilla = OnDisk(DataPath / VanillaFile);
        vanilla.Parent.CreateDirectory();
        await vanilla.WriteAllBytesAsync("vanilla"u8.ToArray());
    }

    protected AbsolutePath OnDisk(GamePath path) => GameInstallation.Locations.ToAbsolutePath(path);

    /// <summary>Installs the entries as one mod and returns its group.</summary>
    protected async Task<LoadoutItemGroup.ReadOnly> InstallMod(Loadout.ReadOnly loadout, Dictionary<RelativePath, byte[]> entries)
    {
        var archive = await CreateTestArchive(entries);
        var library = await RegisterLocalArchive(archive.Path);
        var result = await LoadoutManager.InstallItem(library.AsLibraryFile().AsLibraryItem(), loadout.LoadoutId);
        return result.LoadoutItemGroup!.Value;
    }

    protected async Task<Loadout.ReadOnly> CreateLoadoutOn(string backend)
    {
        var loadout = await CreateLoadout();
        if (backend == "copy")
        {
            var root = new GamePath(LocationId.Game, "");
            await SynchronizerService.ChangeDeploymentBackend(GameInstallation, root, DeploymentBackendId.Copy, chosen: true);
            var plans = ServiceProvider.GetRequiredService<IDeploymentPlanStore>();
            plans.Load(Connection.Db, GameRegistry.ForceGetMetadata(GameInstallation).Id).BackendFor(root)
                .Should().Be(DeploymentBackendId.Copy);
        }
        return loadout;
    }

    [Theory]
    [InlineData("default")]
    [InlineData("copy")]
    public async Task LooseFilesAreDeployedAndThenRemoved(string backend)
    {
        var loadout = await CreateLoadoutOn(backend);
        var texture = OnDisk(DataPath / "textures/t.dds");
        var mesh = OnDisk(DataPath / "meshes/m.nif");
        var vanilla = OnDisk(DataPath / VanillaFile);

        var group = await InstallMod(loadout, new Dictionary<RelativePath, byte[]>
        {
            { "textures/t.dds", "texture"u8.ToArray() },
            { "meshes/m.nif", "mesh"u8.ToArray() },
        });
        await Synchronizer.Synchronize(Refresh(loadout));

        texture.FileExists.Should().BeTrue();
        mesh.FileExists.Should().BeTrue();
        (await texture.ReadAllBytesAsync()).Should().Equal("texture"u8.ToArray());

        await LoadoutManager.RemoveItems([group.LoadoutItemGroupId]);
        await Synchronizer.Synchronize(Refresh(loadout));

        texture.FileExists.Should().BeFalse();
        mesh.FileExists.Should().BeFalse();
        vanilla.FileExists.Should().BeTrue();
        (await vanilla.ReadAllBytesAsync()).Should().Equal("vanilla"u8.ToArray());
    }
}
