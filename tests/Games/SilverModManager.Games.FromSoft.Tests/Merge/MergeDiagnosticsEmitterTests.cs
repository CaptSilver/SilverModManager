using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.FromSoft.Diagnostics;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;
using NexusMods.Paths;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

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
        diagnostic.Id.Should().Be(new DiagnosticId("SilverModManager.Games.FromSoft", 11));
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
        await InstallPackage(loadout.LoadoutId, "Alpha", (RegulationPath, [1, 2, 3]));
        await InstallPackage(loadout.LoadoutId, "Beta", (RegulationPath, [4, 5, 6]));
        loadout = Refresh(loadout);

        var diagnostic = (await Run(loadout)).Should().ContainSingle().Which;
        var act = async () => await Synchronizer.Synchronize(loadout);
        var thrown = (await act.Should().ThrowAsync<MergeRefusedException>()).Which;

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Critical);
        diagnostic.Id.Should().Be(new DiagnosticId("SilverModManager.Games.FromSoft", 12));
        var data = diagnostic.Should().BeOfType<Diagnostic<FromSoftDiagnostics.MergeRefusedMessageData>>().Which.MessageData;
        data.Message.Should().Be(thrown.Message);
        data.Path.Should().Be(RegulationPath);
        data.Strategy.Should().Be("param-rows");
        diagnostic.Actions.Should().BeEmpty("only a refusal for a missing vanilla has something to offer");
    }

    [Fact]
    public async Task AMergeThatFailsIsAWarningNamingTheWinnerByPriorityAndWhyTheMergeFailed()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout.LoadoutId, "Alpha", (Msg, [1, 2, 3]));
        await InstallPackage(loadout.LoadoutId, "Beta", (Msg, [4, 5, 6]));
        loadout = Refresh(loadout);

        var diagnostic = (await Run(loadout)).Should().ContainSingle().Which;

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostic.Id.Should().Be(new DiagnosticId("SilverModManager.Games.FromSoft", 16));
        var data = diagnostic.Should().BeOfType<Diagnostic<FromSoftDiagnostics.PackageWinnerByPriorityMessageData>>().Which.MessageData;
        data.Path.Should().Be(Msg);
        data.Winner.Should().Be("Beta.zip");
        data.Ignored.Should().Be("Alpha.zip");
        data.Reason.Should().StartWith("the merge failed: ");
    }

    [Fact]
    public async Task AMissingVanillaIsACriticalThatOffersToCheckForUpdatesAndToDisableTheModsOfThatBuild()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout.LoadoutId, "Alpha", (RegulationPath, Regulation(GameVersion, 0x10)));
        var beta = await InstallPackage(loadout.LoadoutId, "Beta", (RegulationPath, Regulation(OlderVersion, 0x20)));
        loadout = Refresh(loadout);

        var diagnostic = (await Run(loadout)).Should().ContainSingle().Which;
        var act = async () => await Synchronizer.Synchronize(loadout);
        var thrown = (await act.Should().ThrowAsync<MergeRefusedException>()).Which;

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Critical);
        diagnostic.Id.Should().Be(new DiagnosticId("SilverModManager.Games.FromSoft", 15));
        var data = diagnostic.Should().BeOfType<Diagnostic<FromSoftDiagnostics.VanillaMissingMessageData>>().Which.MessageData;
        data.Message.Should().Be(thrown.Message);
        data.Path.Should().Be(RegulationPath);
        var betaId = beta.AsLoadoutItem().LoadoutItemId;
        diagnostic.Actions.Should().Equal(
            VanillaMissingActions.CheckModUpdates([betaId]),
            VanillaMissingActions.DisableMods(OlderVersion, [betaId], onlyBuild: true));
        diagnostic.Actions[0].Id.Should().Be("fromsoft-check-mod-updates");
        diagnostic.Actions[1].Id.Should().Be("fromsoft-disable-mods");
        diagnostic.Actions[1].Label.Should().Be("Disable these mods in this loadout");
    }

    [Fact]
    public async Task EveryBuildMissingItsVanillaGetsItsOwnDisableActionAndTheUpdateCheckCoversThemAll()
    {
        var loadout = await CreateLoadout();
        var alpha = await InstallPackage(loadout.LoadoutId, "Alpha", (RegulationPath, Regulation(OlderVersion, 0x10)));
        var beta = await InstallPackage(loadout.LoadoutId, "Beta", (RegulationPath, Regulation("11602000", 0x20)));
        loadout = Refresh(loadout);
        var alphaId = alpha.AsLoadoutItem().LoadoutItemId;
        var betaId = beta.AsLoadoutItem().LoadoutItemId;

        var diagnostic = (await Run(loadout)).Should().ContainSingle().Which;

        diagnostic.Actions.Select(a => a.Id).Should().Equal("fromsoft-check-mod-updates", "fromsoft-disable-mods", "fromsoft-disable-mods");
        VanillaMissingActions.ParseIds(diagnostic.Actions[0].Arguments).Should().BeEquivalentTo(new[] { alphaId, betaId });
        diagnostic.Actions[1].Label.Should().Be($"Disable the mods built for {OlderVersion}");
        VanillaMissingActions.ParseIds(diagnostic.Actions[1].Arguments).Should().Equal(alphaId);
        diagnostic.Actions[2].Label.Should().Be("Disable the mods built for 11602000");
        VanillaMissingActions.ParseIds(diagnostic.Actions[2].Arguments).Should().Equal(betaId);
    }

    [Fact]
    public void TheIdsAnActionCarriesRoundTripAndAnythingElseIsRefused()
    {
        var ids = new[] { LoadoutItemId.From(EntityId.From(0x0100000000000ABC)), LoadoutItemId.From(EntityId.From(7)) };

        VanillaMissingActions.ParseIds(VanillaMissingActions.CheckModUpdates(ids).Arguments).Should().Equal(ids);
        var act = () => VanillaMissingActions.ParseIds(["12", "not an id"]);
        act.Should().Throw<ArgumentException>().WithMessage("*not an id*");
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
