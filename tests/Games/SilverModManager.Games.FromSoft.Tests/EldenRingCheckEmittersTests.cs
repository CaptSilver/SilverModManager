using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Games.FromSoft.Diagnostics;
using NexusMods.Games.FromSoft.Doctor;
using NexusMods.Games.FromSoft.Tests.Fixtures;
using NexusMods.Platform.Proton.Doctor;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FromSoft.Tests;

public class EldenRingCheckEmittersTests(ITestOutputHelper helper) : AModdedEldenRingTest<EldenRingCheckEmittersTests>(helper)
{
    private async Task<Diagnostic[]> Diagnose<TEmitter>(Loadout.ReadOnly loadout) where TEmitter : IFromSoftDiagnosticEmitter
    {
        var emitter = ServiceProvider.GetServices<IFromSoftDiagnosticEmitter>().OfType<TEmitter>().Single();
        var found = new List<Diagnostic>();
        await foreach (var diagnostic in emitter.Diagnose(loadout, CancellationToken.None)) found.Add(diagnostic);
        return found.ToArray();
    }

    private void WriteInGameFolder(string relative)
    {
        var path = RealGameFolder / relative;
        path.Parent.CreateDirectory();
        File.WriteAllText(path.GetFullPath(), "x");
    }

    [Fact]
    public async Task AVanillaGameFolderHasNoDiagnostics()
    {
        var loadout = await CreateLoadout();

        (await Diagnose<GameFolderArtifactsEmitter>(loadout)).Should().BeEmpty();
        (await Diagnose<SpawnerEmitter>(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task ModFilesBesideTheLauncherAreCriticalAndNameTheFile()
    {
        var loadout = await CreateLoadout();
        WriteInGameFolder("dinput8.dll");

        var diagnostic = (await Diagnose<GameFolderArtifactsEmitter>(loadout)).Should().ContainSingle().Subject;

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Critical);
        diagnostic.Id.Should().Be(new DiagnosticId("NexusMods.Games.FromSoft", 6));
        diagnostic.FormatDetails(ServiceProvider.GetRequiredService<IDiagnosticWriter>()).Should().Contain("dinput8.dll");
        diagnostic.Actions.Should().Equal(GameCleanupActions.ShowSteps());
    }

    [Fact]
    public async Task ModFilesWithoutTheLauncherAreOnlyAWarning()
    {
        var loadout = await CreateLoadout();
        File.Delete((RealGameFolder / "start_protected_game.exe").GetFullPath());
        WriteInGameFolder("dinput8.dll");

        var diagnostic = (await Diagnose<GameFolderArtifactsEmitter>(loadout)).Should().ContainSingle().Subject;

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostic.Actions.Should().Equal(GameCleanupActions.ShowSteps());
    }

    [Fact]
    public async Task AMissingGameFolderIsNotReportedAsModFiles()
    {
        var loadout = await CreateLoadout();
        Directory.Delete(RealGameFolder.GetFullPath(), recursive: true);

        (await Diagnose<GameFolderArtifactsEmitter>(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task ASpawnerIsAWarning()
    {
        var loadout = await CreateLoadout();
        WriteInGameFolder("mods/ErMerchant.dll");

        var diagnostic = (await Diagnose<SpawnerEmitter>(loadout)).Should().ContainSingle().Subject;

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostic.Id.Should().Be(new DiagnosticId("NexusMods.Games.FromSoft", 7));
    }

    [Fact]
    public async Task MissingSandboxSupportIsCriticalForAModdedLoadout()
    {
        var loadout = await CreateModdedLoadout();
        Probe.Failure = "it exited with code 1: Operation not permitted";

        var diagnostic = (await Diagnose<OverlaySupportEmitter>(loadout)).Should().ContainSingle().Subject;

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Critical);
        diagnostic.Id.Should().Be(new DiagnosticId("NexusMods.Games.FromSoft", 8));
        diagnostic.FormatDetails(ServiceProvider.GetRequiredService<IDiagnosticWriter>()).Should().Contain("Operation not permitted");
    }

    [Fact]
    public async Task MissingSandboxSupportIsNotReportedForAnUnmoddedLoadout()
    {
        var loadout = await CreateLoadout();
        Executables.Bwrap = null;

        (await Diagnose<OverlaySupportEmitter>(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task WorkingSandboxSupportHasNoDiagnostic()
    {
        var loadout = await CreateModdedLoadout();

        (await Diagnose<OverlaySupportEmitter>(loadout)).Should().BeEmpty();
    }
}
