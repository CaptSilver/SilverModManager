using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

namespace SilverModManager.Abstractions.Games.Merge.Tests;

public class EngineMergeReportsTests(IServiceProvider provider) : AGameTest<StubbedGame>(provider)
{
    private sealed class RecordingEngine(MergeReport report) : IMergeEngine
    {
        public List<(LoadoutId Loadout, MergeSourceMode Mode)> Asked { get; } = [];

        public bool Serves(IGameData game) => true;

        public MergeReport Report(Loadout.ReadOnly loadout, MergeSourceMode mode)
        {
            Asked.Add((loadout.LoadoutId, mode));
            return report;
        }

        public IReadOnlyList<MergedOutput> Plan(Loadout.ReadOnly loadout, MergeSourceMode mode) => throw new NotSupportedException();
        public MergePlanReport PlanAndReport(Loadout.ReadOnly loadout, MergeSourceMode mode) => throw new NotSupportedException();
        public IReadOnlyList<PlannedOutput> PlannedOutputs(Loadout.ReadOnly loadout) => throw new NotSupportedException();
        public IReadOnlyList<ContestedPath> NotContested(Loadout.ReadOnly loadout) => throw new NotSupportedException();
        public byte[]? MergedBytes(Loadout.ReadOnly loadout, string relPath) => throw new NotSupportedException();
    }

    [Fact]
    public async Task TheReportComesFromTheEngineThatServesTheLoadoutsGame()
    {
        var loadout = await CreateLoadout();
        var report = new MergeReport([], null, "hint");
        var engine = new RecordingEngine(report);

        var result = await new EngineMergeReports(new MergeEngines([engine]), Connection).ForLoadout(loadout.LoadoutId, CancellationToken.None);

        result.Should().BeSameAs(report);
        engine.Asked.Should().Equal((loadout.LoadoutId, MergeSourceMode.Report));
    }

    [Fact]
    public async Task AGameWithNoEngineIsAnError()
    {
        var loadout = await CreateLoadout();

        var act = async () => await new EngineMergeReports(new MergeEngines([]), Connection).ForLoadout(loadout.LoadoutId, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain(loadout.InstallationInstance.GetGame().DisplayName);
    }
}
