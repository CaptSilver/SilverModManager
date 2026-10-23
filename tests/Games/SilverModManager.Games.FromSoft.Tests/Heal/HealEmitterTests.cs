using System.Collections.Frozen;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.FromSoft.Build;
using NexusMods.Games.FromSoft.Diagnostics;
using NexusMods.Games.FromSoft.Heal;
using NexusMods.Games.FromSoft.Tests.Fixtures;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FromSoft.Tests.Heal;

public class HealEmitterTests(ITestOutputHelper helper) : AVanillaDumpTest<HealEmitterTests>(helper)
{
    private const string RegulationPath = "regulation.bin";

    private BuildStampStore Stamps => ServiceProvider.GetRequiredService<BuildStampStore>();

    private BuildIdentity Live => BuildIdentity.Identify(GameInstallation);

    private async Task<Diagnostic[]> Diagnose(Loadout.ReadOnly loadout)
    {
        var emitter = ServiceProvider.GetServices<IFromSoftDiagnosticEmitter>().OfType<HealEmitter>().Single();
        var found = new List<Diagnostic>();
        await foreach (var diagnostic in emitter.Diagnose(loadout, FrozenDictionary<GamePath, SyncNode>.Empty, CancellationToken.None))
            found.Add(diagnostic);
        return found.ToArray();
    }

    private async Task<Loadout.ReadOnly> LoadoutWithARegulation()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout.LoadoutId, "Alpha", (RegulationPath, Regulation(GameVersion, 0x10)));
        return Refresh(loadout);
    }

    // What the install looked like before a patch moved both the executable and the regulation.
    private BuildIdentity BeforeAPatch() => Live with { Exe = "2.6.0.0", SteamBuildId = "1", RegulationVersion = OlderVersion, RegulationHash = new string('b', 64) };

    [Fact]
    public async Task APatchedInstallWithARegulationWarnsWithEveryStep()
    {
        var loadout = await LoadoutWithARegulation();
        await Stamps.Record(loadout.InstallationId, BeforeAPatch());

        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle().Which;

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostic.Id.Should().Be(new DiagnosticId("NexusMods.Games.FromSoft", 13));
        var steps = HealPlan.Plan(BeforeAPatch(), Live, Formats);
        var data = diagnostic.Should().BeOfType<Diagnostic<FromSoftDiagnostics.HealPlanMessageData>>().Which.MessageData;
        foreach (var step in steps) data.Steps.Should().Contain(step.Detail);
    }

    [Fact]
    public async Task APatchedInstallWithNoRegulationInTheLoadoutIsLeftToTheDriftWarning()
    {
        var loadout = await CreateLoadout();
        await Stamps.Record(loadout.InstallationId, BeforeAPatch());

        (await Diagnose(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task ATamperedInstallIsCriticalWithTheTextThePlanRefusesWith()
    {
        var loadout = await LoadoutWithARegulation();
        var stamped = Live with { RegulationVersion = OlderVersion, RegulationHash = new string('b', 64) };
        await Stamps.Record(loadout.InstallationId, stamped);

        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle().Which;

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Critical);
        diagnostic.Id.Should().Be(new DiagnosticId("NexusMods.Games.FromSoft", 14));
        var data = diagnostic.Should().BeOfType<Diagnostic<FromSoftDiagnostics.HealRefusedMessageData>>().Which.MessageData;
        data.Message.Should().Be(HealPlan.TamperedRefusal(Live, Formats));
        HealPlan.Plan(stamped, Live, Formats).Single().Detail.Should().Be(data.Message);
    }

    [Fact]
    public async Task AnUnchangedInstallEmitsNothing()
    {
        var loadout = await LoadoutWithARegulation();
        await Stamps.Record(loadout.InstallationId, Live);

        (await Diagnose(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnstampedInstallEmitsNothing()
    {
        var loadout = await LoadoutWithARegulation();

        (await Diagnose(loadout)).Should().BeEmpty();
    }
}
