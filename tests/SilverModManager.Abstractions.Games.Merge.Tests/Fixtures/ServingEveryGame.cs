using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games.Merge.Tests.Fixtures;

/// <summary>A real engine that also serves the stub game, which does not implement <see cref="IHasMergeGame"/>.</summary>
internal sealed class ServingEveryGame(IMergeEngine inner) : IMergeEngine
{
    public bool Serves(IGameData game) => true;
    public MergeReport Report(Loadout.ReadOnly loadout, MergeSourceMode mode) => inner.Report(loadout, mode);
    public IReadOnlyList<MergedOutput> Plan(Loadout.ReadOnly loadout, MergeSourceMode mode) => inner.Plan(loadout, mode);
    public MergePlanReport PlanAndReport(Loadout.ReadOnly loadout, MergeSourceMode mode) => inner.PlanAndReport(loadout, mode);
    public IReadOnlyList<PlannedOutput> PlannedOutputs(Loadout.ReadOnly loadout) => inner.PlannedOutputs(loadout);
    public IReadOnlyList<ContestedPath> NotContested(Loadout.ReadOnly loadout) => inner.NotContested(loadout);
    public byte[]? MergedBytes(Loadout.ReadOnly loadout, string relPath) => inner.MergedBytes(loadout, relPath);
}
