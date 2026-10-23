using FluentAssertions;
using SilverModManager.Games.FromSoft.Build;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Heal;
using SilverModManager.Games.FromSoft.Tests.Build;

namespace SilverModManager.Games.FromSoft.Tests.Heal;

public class HealPlanTests
{
    private static readonly BuildIdentity Stamped = BuildIds.Make(exe: "2.6.0.0", steamBuildId: "100", regulationVersion: "11601000", regulationHash: new string('b', 64));
    private static readonly BuildIdentity Patched = BuildIds.Make(exe: "2.7.0.0", steamBuildId: "200", regulationVersion: "11701000", regulationHash: new string('c', 64));

    private static readonly FromSoftFormatTraits Traits = EldenRingGame.Definition.Formats;

    private static HealStepKind[] Kinds(IEnumerable<HealStep> steps) => steps.Select(s => s.Kind).ToArray();

    [Fact]
    public void NoDriftPlansNothing()
        => HealPlan.Plan(Stamped, Stamped, Traits).Should().BeEmpty();

    [Fact]
    public void AnUnstampedInstallPlansNothing()
        => HealPlan.Plan(null, Patched, Traits).Should().BeEmpty();

    [Fact]
    public void APatchPlansTheFullRebaseInOrder()
    {
        var steps = HealPlan.Plan(Stamped, Patched, Traits);

        Kinds(steps).Should().Equal(
            HealStepKind.AdoptBaseline, HealStepKind.CheckForModUpdates, HealStepKind.Gate,
            HealStepKind.Rebuild, HealStepKind.Verify, HealStepKind.Stamp);
        steps[0].Detail.Should().Contain("11701000");
    }

    [Fact]
    public void TheGateRunsAfterCheckingForModUpdatesNotBefore()
    {
        var kinds = Kinds(HealPlan.Plan(Stamped, Patched, Traits));

        // An updated mod may be exactly what makes its rows fit the new baseline.
        kinds.Should().ContainInOrder(HealStepKind.CheckForModUpdates, HealStepKind.Gate);
    }

    [Fact]
    public void ARepackagedBuildRestampsWithoutRebuilding()
    {
        var repackaged = Stamped with { Exe = "2.7.0.0", SteamBuildId = "200" };

        Kinds(HealPlan.Plan(Stamped, repackaged, Traits)).Should().Equal(HealStepKind.CheckForModUpdates, HealStepKind.Stamp);
    }

    [Fact]
    public void ATamperedInstallRefusesAndPlansNothingElse()
    {
        var tampered = Stamped with { RegulationVersion = "11701000", RegulationHash = new string('c', 64) };

        var steps = HealPlan.Plan(Stamped, tampered, Traits);

        var refusal = steps.Should().ContainSingle().Which;
        refusal.Kind.Should().Be(HealStepKind.Refuse);
        refusal.Detail.Should().Be(HealPlan.TamperedRefusal(tampered, Traits));
        refusal.Detail.Should().Contain("regulation.bin").And.Contain(tampered.Exe).And.Contain(tampered.SteamBuildId);
    }

    [Fact]
    public void ThePlanNeverNamesTheLauncherSwap()
        => HealPlan.Plan(Stamped, Patched, Traits).Select(s => s.Detail).Should().NotContain(d => d.Contains("reharden", StringComparison.OrdinalIgnoreCase));
}
