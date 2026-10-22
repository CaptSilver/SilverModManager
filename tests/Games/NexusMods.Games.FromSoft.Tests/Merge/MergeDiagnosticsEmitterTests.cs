using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.FromSoft.Diagnostics;
using NexusMods.Games.FromSoft.Merge;
using NexusMods.Games.FromSoft.Tests.Fixtures;
using NexusMods.Games.FromSoft.Tests.Formats.Fixtures;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FromSoft.Tests.Merge;

public class MergeDiagnosticsEmitterTests(ITestOutputHelper helper) : AVanillaDumpTest<MergeDiagnosticsEmitterTests>(helper)
{
    private const string Msg = "msg/engus/item.msgbnd.dcx";
    private const string RegulationPath = "regulation.bin";

    private MergeDiagnosticsEmitter Emitter => ServiceProvider.GetServices<IFromSoftDiagnosticEmitter>().OfType<MergeDiagnosticsEmitter>().Single();

    private AbsolutePath Mods => GameInstallation.Locations[LocationId.Me3Mods].Path;

    private static byte[] Msgbnd(string text)
        => MsgbndFixture.Build(new Dictionary<int, IReadOnlyDictionary<int, string?>>
        {
            [1] = new Dictionary<int, string?> { [10] = text },
        }, Formats);

    // The game's own regulation with the first member replaced by bytes that aren't a param table.
    private static byte[] RegulationWithFirstMember(string content)
        => RegulationFixture.BuildFromEntries(
            RegulationFixture.GoldenEntries.Select(e => e.Id == 1 ? (e.Id, e.Name, System.Text.Encoding.ASCII.GetBytes(content)) : e).ToArray(),
            GameVersion, Formats);

    private async Task<Diagnostic[]> Run(Loadout.ReadOnly loadout)
    {
        var diagnostics = new List<Diagnostic>();
        await foreach (var diagnostic in Emitter.Diagnose(Refresh(loadout), CancellationToken.None)) diagnostics.Add(diagnostic);
        return diagnostics.ToArray();
    }

    [Fact]
    public async Task TwoMsgbndsWithDifferentTextRaiseOneWarningWithTheMergesSentence()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout.LoadoutId, "Alpha", (Msg, Msgbnd("alpha text")));
        await InstallPackage(loadout.LoadoutId, "Beta", (Msg, Msgbnd("beta text")));
        loadout = Refresh(loadout);
        var note = ServiceProvider.GetRequiredService<MergePlanCache>().Current(loadout, MergeSourceMode.Report).Notes.Single();

        var diagnostic = (await Run(loadout)).Should().ContainSingle().Which;

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostic.Id.Should().Be(new DiagnosticId("NexusMods.Games.FromSoft", 11));
        var data = diagnostic.Should().BeOfType<Diagnostic<FromSoftDiagnostics.MergeNoteMessageData>>().Which.MessageData;
        data.Path.Should().Be(Msg);
        data.Mods.Should().Contain("Alpha").And.Contain("Beta");
        data.Note.Should().Be(note.Note.Describe());
    }

    [Fact]
    public async Task AParamTableTakenWholeIsAWarningAndWhereTheVanillaCameFromIsNotMentioned()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout.LoadoutId, "Alpha", (RegulationPath, RegulationWithFirstMember("BBBB")));
        await InstallPackage(loadout.LoadoutId, "Beta", (RegulationPath, EldenRingInstall.RegulationFile));
        ServiceProvider.GetRequiredService<MergePlanCache>().Current(Refresh(loadout), MergeSourceMode.Report).Notes
            .Select(n => n.Note).Should().Contain(n => n is VanillaServedFromDump, "the plan does say it, so its absence below is the emitter leaving it out");

        var diagnostics = await Run(loadout);

        var data = diagnostics.Should().ContainSingle().Which.Should().BeOfType<Diagnostic<FromSoftDiagnostics.MergeNoteMessageData>>().Which.MessageData;
        data.Path.Should().Be(RegulationPath);
        data.Note.Should().Be(new UnreadableParam(1).Describe());
    }

    [Fact]
    public async Task ARefusedMergeIsACriticalWhoseTextIsTheMessageTheApplyThrows()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout.LoadoutId, "Alpha", (RegulationPath, Regulation(GameVersion, 0x10)));
        await InstallPackage(loadout.LoadoutId, "Beta", (RegulationPath, Regulation(OlderVersion, 0x20)));
        loadout = Refresh(loadout);

        var diagnostic = (await Run(loadout)).Should().ContainSingle().Which;
        var act = async () => await Synchronizer.Synchronize(loadout);
        var thrown = (await act.Should().ThrowAsync<MergeRefusedException>()).Which;

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Critical);
        diagnostic.Id.Should().Be(new DiagnosticId("NexusMods.Games.FromSoft", 12));
        var data = diagnostic.Should().BeOfType<Diagnostic<FromSoftDiagnostics.MergeRefusedMessageData>>().Which.MessageData;
        data.Message.Should().Be(thrown.Message);
        data.Path.Should().Be(RegulationPath);
        data.Strategy.Should().Be("param-rows");
    }

    [Fact]
    public async Task ALoadoutWithNoOverlapEmitsNothing()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout.LoadoutId, "Alpha", ("parts/alpha.dcx", [1, 2, 3]));
        await InstallPackage(loadout.LoadoutId, "Beta", ("parts/beta.dcx", [4, 5, 6]));

        (await Run(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task RunningTheEmitterWritesNothingToTheDataRootOrTheModsFolder()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout.LoadoutId, "Alpha", (RegulationPath, RegulationWithFirstMember("BBBB")));
        await InstallPackage(loadout.LoadoutId, "Beta", (RegulationPath, EldenRingInstall.RegulationFile));
        var mods = Mods.DirectoryExists() ? TreeSnapshot.Take(Mods) : [];

        await Run(loadout);

        (DataRoot / "baselines").DirectoryExists().Should().BeFalse("a report only says what an apply would keep");
        (Mods.DirectoryExists() ? TreeSnapshot.Take(Mods) : []).Should().Equal(mods);
    }
}
