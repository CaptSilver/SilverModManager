using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.TestFramework;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.StandardGameLocators.TestHelpers;
using Xunit;

namespace NexusMods.Games.CreationEngine.Tests.OblivionRemastered;

/// <summary>The game runs through its script extender loader in Binaries/Win64 when the loadout holds it.</summary>
public class OblivionRemasteredToolTests(ITestOutputHelper helper)
    : AIsolatedGameTest<OblivionRemasteredToolTests, CreationEngine.OblivionRemastered.OblivionRemastered>(helper)
{
    private static readonly GamePath Loader = new(LocationId.Game, "OblivionRemastered/Binaries/Win64/obse64_loader.exe");

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddCreationEngine()
            .AddUniversalGameLocator<CreationEngine.OblivionRemastered.OblivionRemastered>(new Version("1.2.0.0"), locationsFileSystem: IsolatedKnownFolders.Create);
    }

    private RunGameTool<CreationEngine.OblivionRemastered.OblivionRemastered> Tool => ServiceProvider.GetServices<ITool>()
        .OfType<RunGameViaScriptExtenderTool<CreationEngine.OblivionRemastered.OblivionRemastered>>().Single();

    [Fact]
    public async Task LoaderInTheLoadout_ResolvesToTheLoaderInBinaries()
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            var group = AddEmptyGroup(tx, loadout, "OBSE64");
            AddFile(tx, loadout, group, Loader);
            await tx.Commit();
        }
        Refresh(ref loadout);

        var spec = await Tool.GetLaunchSpec(loadout, CancellationToken.None);

        spec.Exe.Should().Be(GameInstallation.Locations.ToAbsolutePath(Loader));
    }

    [Fact]
    public async Task NoLoaderInTheLoadout_ResolvesToTheLauncher()
    {
        var loadout = await CreateLoadout();

        var spec = await Tool.GetLaunchSpec(loadout, CancellationToken.None);

        spec.Exe.Should().Be(GameInstallation.Locations.ToAbsolutePath(Game.GetPrimaryFile(GameInstallation)));
    }
}
