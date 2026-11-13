using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Games.FromSoft.DarkSouls3;
using SilverModManager.Games.FromSoft.Launch;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.FromSoft.Tests;

public class DarkSouls3Tests(ITestOutputHelper helper)
    : AFromSoftInstalledGameTests<DarkSouls3Tests, DarkSouls3Game>(helper, FromSoftGameFacts.DarkSouls3)
{
    [Fact]
    public async Task TheRegulationGateSkipsTheGameWhileItHasNoRegulationName()
    {
        var gameFolder = Install.Folder / "Game";
        gameFolder.CreateDirectory();
        await (gameFolder / "Data0.bdt").WriteAllBytesAsync([1, 2, 3]);
        var loadout = await CreateLoadout();
        var gate = ServiceProvider.GetServices<ILaunchGate>().OfType<RegulationGate>().Single();

        gate.Check(new LaunchContext(GameInstallation, loadout.LoadoutId, loadout.Name, [])).Should().BeNull();
    }

    [Fact]
    public void EveryGamePathIsNeverWrittenAndNoMe3PathIs()
    {
        Synchronizer.IsIgnoredBackupPath(new GamePath(LocationId.Game, "Game/DarkSoulsIII.exe")).Should().BeTrue();
        Synchronizer.IsIgnoredBackupPath(new GamePath(LocationId.Me3Mods, "parts/x.dcx")).Should().BeFalse();
    }
}
