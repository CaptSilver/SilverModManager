using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Steam;
using SilverModManager.Games.FromSoft.Build;
using SilverModManager.Games.FromSoft.Heal;
using SilverModManager.Games.FromSoft.Launch;
using SilverModManager.Games.FromSoft.Overlay;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Games.FromSoft.Tests.Launch;

/// <summary>The fixture installs Elden Ring from Steam manifests that list exactly the files it writes, so the installed regulation is vouched for until a test changes it.</summary>
public class RegulationGateTests(ITestOutputHelper helper) : AModdedEldenRingTest<RegulationGateTests>(helper)
{
    private RegulationGate RegulationGateUnderTest => ServiceProvider.GetServices<ILaunchGate>().OfType<RegulationGate>().Single();

    private VanillaSaveGate SaveGate => ServiceProvider.GetServices<ILaunchGate>().OfType<VanillaSaveGate>().Single();

    private void KeepTheInstalledRegulationAsBaseline()
        => ServiceProvider.GetRequiredService<RegulationBaselines>().Adopt(GameInstallation, BuildIdentity.Identify(GameInstallation));

    private void ModifyTheRegulation()
    {
        var regulation = RealGameFolder / "regulation.bin";
        var bytes = File.ReadAllBytes(regulation.GetFullPath());
        bytes[^1] ^= 1;
        File.WriteAllBytes(regulation.GetFullPath(), bytes);
    }

    [Fact]
    public async Task AVanillaLaunchWithARegulationSteamDidNotShipIsRefused()
    {
        var loadout = await CreateLoadout();
        ModifyTheRegulation();

        var veto = RegulationGateUnderTest.Check(ContextOf(loadout));

        veto!.Message.Should().Contain("Game/regulation.bin").And.Contain("plays online").And.Contain("without mods");
    }

    [Fact]
    public async Task TheVetoOffersTheRestoreWhenABaselineIsOnHand()
    {
        var loadout = await CreateLoadout();
        KeepTheInstalledRegulationAsBaseline();
        ModifyTheRegulation();

        RegulationGateUnderTest.Check(ContextOf(loadout))!.Message.Should().Contain("Restore the game's own regulation from Health Check");
    }

    [Fact]
    public async Task TheVetoSendsTheUserToSteamWhenNoBaselineIsOnHand()
    {
        var loadout = await CreateLoadout();
        foreach (var kept in ServiceProvider.GetRequiredService<RegulationBaselines>().List()) File.Delete(kept.File.GetFullPath());
        ModifyTheRegulation();

        RegulationGateUnderTest.Check(ContextOf(loadout))!.Message.Should().Contain("Verify the game's files in Steam");
    }

    [Fact]
    public async Task AVouchedRegulationPasses()
    {
        var loadout = await CreateLoadout();

        RegulationGateUnderTest.Check(ContextOf(loadout)).Should().BeNull();
    }

    [Fact]
    public async Task AModdedLaunchIsNeverRefusedHere()
    {
        var loadout = await CreateModdedLoadout();
        ModifyTheRegulation();

        RegulationGateUnderTest.Check(ContextOf(loadout)).Should().BeNull();
    }

    [Fact]
    public async Task AMissingRegulationFilePasses()
    {
        var loadout = await CreateLoadout();
        File.Delete((RealGameFolder / "regulation.bin").GetFullPath());

        RegulationGateUnderTest.Check(ContextOf(loadout)).Should().BeNull();
    }

    [Fact]
    public async Task WithoutAReadableManifestTheLaunchGoesAhead()
    {
        var loadout = await CreateLoadout();
        ModifyTheRegulation();
        var vouching = Substitute.For<IManifestVouching>();
        vouching.InstalledFiles(Arg.Any<SilverModManager.Sdk.Games.GameInstallation>()).Returns((IReadOnlyList<ManifestFile>?)null);
        var gate = new RegulationGate(Connection, vouching, ServiceProvider.GetRequiredService<RegulationRestore>(),
            ServiceProvider.GetRequiredService<BuildStampStore>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<RegulationGate>.Instance);

        gate.Check(ContextOf(loadout)).Should().BeNull();
    }

    [Fact]
    public void TheGateIsRegisteredAfterTheSaveGate()
    {
        var order = ServiceProvider.GetServices<ILaunchGate>()
            .Where(gate => gate is OverlayLaunchGate or VanillaSaveGate or RegulationGate)
            .Select(gate => gate.GetType())
            .ToArray();

        order.Should().Equal(typeof(OverlayLaunchGate), typeof(VanillaSaveGate), typeof(RegulationGate));
    }

    [Fact]
    public async Task LoaderFilesStillTripTheSaveGateWithAVouchedRegulation()
    {
        var loadout = await CreateLoadout();
        File.WriteAllText((RealGameFolder / "dinput8.dll").GetFullPath(), "proxy");

        SaveGate.Check(ContextOf(loadout))!.Message.Should().Contain("dinput8.dll");
        RegulationGateUnderTest.Check(ContextOf(loadout)).Should().BeNull();
    }

    [Fact]
    public async Task PreparingAndFinishingDoNothing()
    {
        var loadout = await CreateLoadout();

        await RegulationGateUnderTest.Prepare(ContextOf(loadout), CancellationToken.None);
        await RegulationGateUnderTest.Finish(ContextOf(loadout));
    }
}

/// <summary>Test Souls does not play online and names no regulation, so the gate has nothing to check.</summary>
public class RegulationGateOfflineGameTests(ITestOutputHelper helper) : AModdedTestSoulsTest<RegulationGateOfflineGameTests>(helper)
{
    [Fact]
    public async Task AnOfflineGameIsNotChecked()
    {
        var loadout = await CreateLoadout();
        var gate = ServiceProvider.GetServices<ILaunchGate>().OfType<RegulationGate>().Single();

        gate.Check(ContextOf(loadout)).Should().BeNull();
    }
}
