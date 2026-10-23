using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Games.FromSoft.Doctor;
using NexusMods.Games.FromSoft.Overlay;
using NexusMods.Games.FromSoft.Tests.Fixtures;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FromSoft.Tests.Overlay;

public class UpperdirAuditEmitterTests(ITestOutputHelper helper) : AModdedEldenRingTest<UpperdirAuditEmitterTests>(helper)
{
    private UpperdirAuditEmitter Emitter => ServiceProvider.GetRequiredService<UpperdirAuditEmitter>();

    private async Task<Diagnostic[]> Diagnose(Loadout.ReadOnly loadout)
    {
        var found = new List<Diagnostic>();
        await foreach (var diagnostic in Emitter.Diagnose(loadout, System.Collections.Frozen.FrozenDictionary<NexusMods.Sdk.Games.GamePath, NexusMods.Abstractions.Loadouts.Synchronizers.SyncNode>.Empty, CancellationToken.None))
            found.Add(diagnostic);
        return found.ToArray();
    }

    [Fact]
    public async Task ALoadoutWithNothingPendingHasNoDiagnostics()
    {
        var loadout = await CreateModdedLoadout();

        (await Diagnose(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task EveryPendingFindingIsCriticalWithADiscardActionNamingTheLoadoutKindAndPath()
    {
        var loadout = await CreateModdedLoadout();
        var layout = LayoutOf(loadout);
        await Gate.Prepare(ContextOf(loadout), CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(layout.Upper.GetFullPath(), "stray.txt"), "x");
        await File.WriteAllTextAsync(Path.Combine(RealGameFolder.GetFullPath(), "leaked.dll"), "x");
        await Gate.Finish(ContextOf(loadout));

        var diagnostics = await Diagnose(loadout);

        diagnostics.Should().HaveCount(2).And.OnlyContain(d => d.Severity == DiagnosticSeverity.Critical);
        diagnostics.SelectMany(d => d.Actions).Should().BeEquivalentTo(
        [
            new DiagnosticAction(UpperdirAuditEmitter.DiscardActionId, "Discard", [loadout.LoadoutId.Value.Value.ToString(), "Unexpected", "stray.txt"]),
            new DiagnosticAction(UpperdirAuditEmitter.DiscardActionId, "Discard", [loadout.LoadoutId.Value.Value.ToString(), "GameFolderChanged", "leaked.dll"]),
        ]);
    }

    [Fact]
    public async Task DiscardingTheFindingClearsTheDiagnostic()
    {
        var loadout = await CreateModdedLoadout();
        var layout = LayoutOf(loadout);
        await Gate.Prepare(ContextOf(loadout), CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(layout.Upper.GetFullPath(), "stray.txt"), "x");
        await Gate.Finish(ContextOf(loadout));

        UpperdirAudit.Discard(layout, UpperdirAudit.PendingFindings(layout).Single());

        (await Diagnose(loadout)).Should().BeEmpty();
    }
}
