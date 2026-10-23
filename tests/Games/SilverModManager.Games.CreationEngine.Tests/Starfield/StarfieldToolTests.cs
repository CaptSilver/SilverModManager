using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.CreationEngine.Tests.Starfield;

public class StarfieldToolTests(ITestOutputHelper helper) : AIsolatedGameTest<StarfieldToolTests, CreationEngine.Starfield.Starfield>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddCreationEngine()
        .AddUniversalGameLocator<CreationEngine.Starfield.Starfield>(new Version("1.15.222"), locationsFileSystem: IsolatedKnownFolders.Create);

    private RunGameTool<CreationEngine.Starfield.Starfield> Tool => ServiceProvider.GetServices<ITool>()
        .OfType<RunGameViaScriptExtenderTool<CreationEngine.Starfield.Starfield>>().Single();

    [Fact]
    public async Task LoaderInTheLoadout_ResolvesToTheLoader()
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            var group = AddEmptyGroup(tx, loadout, "SFSE");
            AddFile(tx, loadout, group, KnownPaths.SFSELoader);
            await tx.Commit();
        }
        Refresh(ref loadout);

        var spec = await Tool.GetLaunchSpec(loadout, CancellationToken.None);

        spec.Exe.Should().Be(GameInstallation.Locations.ToAbsolutePath(KnownPaths.SFSELoader));
    }

    [Fact]
    public async Task WithoutTheLoader_ResolvesToThePrimaryExecutable()
    {
        var loadout = await CreateLoadout();

        var spec = await Tool.GetLaunchSpec(loadout, CancellationToken.None);

        spec.Exe.Should().Be(GameInstallation.Locations.ToAbsolutePath(Game.GetPrimaryFile(GameInstallation)));
    }
}
