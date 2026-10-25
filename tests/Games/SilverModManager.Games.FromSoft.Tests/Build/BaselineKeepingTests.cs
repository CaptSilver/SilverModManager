using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Games.FromSoft.Build;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Tests.Build;

/// <summary>
/// Where the app keeps a copy of the installed regulation without being asked: wherever it identifies the live build,
/// so a game update cannot take the only copy of the old build with it.
/// </summary>
public class BaselineKeepingTests(ITestOutputHelper helper) : AVanillaDumpTest<BaselineKeepingTests>(helper)
{
    private const string PatchedVersion = "11999999";

    private BuildStampGate StampGate => ServiceProvider.GetServices<ILaunchGate>().OfType<BuildStampGate>().Single();

    private BuildStampStore Stamps => ServiceProvider.GetRequiredService<BuildStampStore>();

    private AbsolutePath Baselines => DataRoot / "baselines";

    private AbsolutePath KeptFile(string version) => Baselines / $"regulation-{version}.bin";

    private void WriteGame(byte[] regulation) => File.WriteAllBytes((RealGameFolder / "regulation.bin").GetFullPath(), regulation);

    // A patch moves the regulation together with the executable and Steam's build id.
    private void Patch(byte[] regulation)
    {
        WriteGame(regulation);
        File.WriteAllBytes((RealGameFolder / "eldenring.exe").GetFullPath(), FakePe.WithVersion(new Version(2, 8, 0, 0)));
        EldenRingInstall.SetSteamBuildId(Install, "99999999");
        Relocate();
    }

    [Fact]
    public async Task TheLaunchGateKeepsTheInstalledRegulationWhenItStampsTheBuild()
    {
        var loadout = await CreateLoadout();

        await StampGate.Prepare(ContextOf(loadout), CancellationToken.None);

        File.ReadAllBytes(KeptFile(GameVersion).GetFullPath()).Should().Equal(EldenRingInstall.RegulationFile);
    }

    [Fact]
    public async Task TheLaunchGateKeepsTheNewBuildAfterAPatchWhileTheStampStaysOnTheOldOne()
    {
        var loadout = await CreateLoadout();
        await StampGate.Prepare(ContextOf(loadout), CancellationToken.None);
        var patched = Regulation(PatchedVersion, 0x77);
        Patch(patched);

        await StampGate.Prepare(ContextOf(loadout), CancellationToken.None);

        File.ReadAllBytes(KeptFile(PatchedVersion).GetFullPath()).Should().Equal(patched);
        File.Exists(KeptFile(GameVersion).GetFullPath()).Should().BeTrue("the old build stays");
    }

    [Fact]
    public async Task TheLaunchGateDoesNotKeepARegulationThatChangedUnderAnUnchangedInstall()
    {
        var loadout = await CreateLoadout();
        await StampGate.Prepare(ContextOf(loadout), CancellationToken.None);
        WriteGame(Regulation(PatchedVersion, 0x77));

        await StampGate.Prepare(ContextOf(loadout), CancellationToken.None);

        File.Exists(KeptFile(PatchedVersion).GetFullPath()).Should().BeFalse();
    }

    [Fact]
    public async Task RestampingKeepsTheLiveBuild()
    {
        var loadout = await CreateLoadout();
        await Stamps.Record(loadout.InstallationId, BuildIdentity.Identify(GameInstallation) with { Exe = "2.6.0.0", SteamBuildId = "1" });
        Baselines.DirectoryExists().Should().BeFalse();

        await Stamps.Restamp(loadout.LoadoutId);

        File.ReadAllBytes(KeptFile(GameVersion).GetFullPath()).Should().Equal(EldenRingInstall.RegulationFile);
    }

    [Fact]
    public async Task RestampingATamperedInstallDoesNotKeepItsRegulation()
    {
        // Re-stamp is offered for a tampered install too; the file something else wrote is not kept on the way.
        var loadout = await CreateLoadout();
        await Stamps.Record(loadout.InstallationId, BuildIdentity.Identify(GameInstallation));
        WriteGame(Regulation(PatchedVersion, 0x77));

        await Stamps.Restamp(loadout.LoadoutId);

        File.Exists(KeptFile(PatchedVersion).GetFullPath()).Should().BeFalse();
    }

    private async Task<Loadout.ReadOnly> TwoRegulationMods()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout.LoadoutId, "Alpha", ("regulation.bin", Regulation(GameVersion, 0x10)));
        await InstallPackage(loadout.LoadoutId, "Beta", ("regulation.bin", Regulation(GameVersion, 0x20)));
        return Refresh(loadout);
    }

    [Fact]
    public async Task PlanningAStampedInstallKeepsItsRegulationEvenWhenOnlyReporting()
    {
        var loadout = await TwoRegulationMods();
        await Stamps.Record(loadout.InstallationId, BuildIdentity.Identify(GameInstallation));

        ServiceProvider.GetRequiredService<MergePlanCache>().Current(loadout, MergeSourceMode.Report);

        File.ReadAllBytes(KeptFile(GameVersion).GetFullPath()).Should().Equal(EldenRingInstall.RegulationFile);
    }

    [Fact]
    public async Task PlanningADriftedInstallKeepsNothing()
    {
        var loadout = await TwoRegulationMods();
        await Stamps.Record(loadout.InstallationId, BuildIdentity.Identify(GameInstallation));
        WriteGame(Regulation(PatchedVersion, 0x77));

        ServiceProvider.GetRequiredService<MergePlanCache>().Current(loadout, MergeSourceMode.Report);

        Baselines.DirectoryExists().Should().BeFalse();
    }
}
