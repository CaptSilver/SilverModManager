using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.StandardGameLocators.TestHelpers;

namespace NexusMods.Games.CreationEngine.Tests.Starfield;

public class StarfieldToolTests(ITestOutputHelper helper) : AIsolatedGameTest<StarfieldToolTests, CreationEngine.Starfield.Starfield>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddCreationEngine()
        .AddUniversalGameLocator<CreationEngine.Starfield.Starfield>(new Version("1.15.222"));

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
