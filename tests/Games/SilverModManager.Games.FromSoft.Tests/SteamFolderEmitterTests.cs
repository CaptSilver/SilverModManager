using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Steam;
using SilverModManager.Games.FromSoft.Diagnostics;
using SilverModManager.Games.FromSoft.Build;
using SilverModManager.Games.FromSoft.Doctor;
using SilverModManager.Games.FromSoft.Heal;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Tests;

/// <summary>The fixture installs Elden Ring from Steam manifests that list exactly the files it writes.</summary>
public class SteamFolderEmitterTests(ITestOutputHelper helper) : AModdedEldenRingTest<SteamFolderEmitterTests>(helper)
{
    private async Task<Diagnostic[]> Diagnose(Loadout.ReadOnly loadout)
    {
        var emitter = ServiceProvider.GetServices<IFromSoftDiagnosticEmitter>().OfType<SteamFolderEmitter>().Single();
        var found = new List<Diagnostic>();
        await foreach (var diagnostic in emitter.Diagnose(loadout, CancellationToken.None)) found.Add(diagnostic);
        return found.ToArray();
    }

    [Fact]
    public async Task AFolderOfSteamsFilesHasNoDiagnostic()
    {
        var loadout = await CreateLoadout();

        (await Diagnose(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task ARegulationSteamDidNotShipIsAWarningThatSaysASteamLaunchTakesItOnline()
    {
        var loadout = await CreateLoadout();
        var regulation = RealGameFolder / "regulation.bin";
        var bytes = File.ReadAllBytes(regulation.GetFullPath());
        bytes[^1] ^= 1;
        File.WriteAllBytes(regulation.GetFullPath(), bytes);

        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle().Subject;

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostic.Id.Should().Be(new DiagnosticId("SilverModManager.Games.FromSoft", 17));
        var details = diagnostic.FormatDetails(ServiceProvider.GetRequiredService<IDiagnosticWriter>());
        details.Should().Contain("regulation.bin").And.Contain("a Steam launch would take it online");
    }

    [Fact]
    public async Task AModLoaderFileIsListedInTheSameDiagnostic()
    {
        var loadout = await CreateLoadout();
        File.WriteAllText((RealGameFolder / "dinput8.dll").GetFullPath(), "hook");

        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle().Subject;

        diagnostic.FormatDetails(ServiceProvider.GetRequiredService<IDiagnosticWriter>()).Should().Contain("dinput8.dll");
    }

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
    public async Task AModifiedRegulationWithAVouchedBaselineOffersTheRestoreAlone()
    {
        var loadout = await CreateLoadout();
        KeepTheInstalledRegulationAsBaseline();
        ModifyTheRegulation();

        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle().Subject;

        diagnostic.Actions.Select(a => a.Id).Should().Equal(RegulationRestore.RestoreActionId);
    }

    [Fact]
    public async Task AModifiedRegulationWithNoBaselineOffersSteamsVerify()
    {
        var loadout = await CreateLoadout();
        foreach (var kept in ServiceProvider.GetRequiredService<RegulationBaselines>().List()) File.Delete(kept.File.GetFullPath());
        ModifyTheRegulation();

        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle().Subject;

        var action = diagnostic.Actions.Should().ContainSingle().Which;
        action.Id.Should().Be(DiagnosticActions.VerifyGameFilesInSteamId);
        action.Arguments.Should().Equal(EldenRingInstall.AppId.ToString());
    }

    [Fact]
    public async Task AModLoaderFileAloneGetsOnlyTheVerifyButton()
    {
        var loadout = await CreateLoadout();
        KeepTheInstalledRegulationAsBaseline();
        File.WriteAllText((RealGameFolder / "dinput8.dll").GetFullPath(), "hook");

        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle().Subject;

        diagnostic.Actions.Select(a => a.Id).Should().Equal(DiagnosticActions.VerifyGameFilesInSteamId);
    }

    [Fact]
    public async Task AModifiedRegulationAndAModLoaderFileGetBothButtons()
    {
        var loadout = await CreateLoadout();
        KeepTheInstalledRegulationAsBaseline();
        ModifyTheRegulation();
        File.WriteAllText((RealGameFolder / "dinput8.dll").GetFullPath(), "hook");

        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle().Subject;

        diagnostic.Actions.Select(a => a.Id).Should().Equal(RegulationRestore.RestoreActionId, DiagnosticActions.VerifyGameFilesInSteamId);
    }

    [Fact]
    public async Task AnUnreadableManifestSaysNothing()
    {
        var loadout = await CreateLoadout();
        File.WriteAllText((RealGameFolder / "dinput8.dll").GetFullPath(), "hook");
        var vouching = ServiceProvider.GetRequiredService<IManifestVouching>();
        var emitter = new SteamFolderEmitter(new NoManifest(vouching), new SteamFolderCheck(Microsoft.Extensions.Logging.Abstractions.NullLogger<SteamFolderCheck>.Instance),
            ServiceProvider.GetRequiredService<RegulationRestore>(), ServiceProvider.GetRequiredService<BuildStampStore>());

        var found = new List<Diagnostic>();
        await foreach (var diagnostic in emitter.Diagnose(loadout, CancellationToken.None)) found.Add(diagnostic);

        found.Should().BeEmpty();
    }

    private sealed class NoManifest(IManifestVouching inner) : IManifestVouching
    {
        public ManifestMatch? Find(GameInstallation installation, Sha1Value hash) => inner.Find(installation, hash);

        public IReadOnlyList<ManifestFile>? InstalledFiles(GameInstallation installation) => null;
    }
}
