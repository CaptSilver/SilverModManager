using SilverModManager.Sdk.Hashes;
using System.Collections.Frozen;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Hashing.xxHash3;
using NSubstitute;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games.Merge.Tests.Fixtures;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

namespace SilverModManager.Abstractions.Games.Merge.Tests;

public class EngineMergeDiagnosticsEmitterTests(IServiceProvider provider) : AGameTest<StubbedGame>(provider)
{
    private const string Script = "scripts/x.ws";

    private static MergeReportEntry Merged(string path = Script) => new(path, "text", ["Alpha", "Beta"], MergeReportOutcome.Merged, [], "the game's own copy", "");

    private static MergeReportEntry NotMergeable(string path, params string[] mods)
        => new(path, "", mods, MergeReportOutcome.NotMergeable, [], "", "not a file the app merges; load order decides");

    private static MergedOutput Output(params IMergeNote[] notes)
        => new(new GamePath(LocationId.Game, $"Mods/merged/{Script}"), Script, [], ["Alpha", "Beta"], notes, "text");

    private static LinesKept Loss => new(Script, 4, 7, "Alpha", "Beta");

    private static ContestedPath Bundle(params string[] mods)
        => new("content/blob0.bundle", mods.Select(m => new ContestedFile(m, "content/blob0.bundle", new GamePath(LocationId.Game, $"Mods/{m}/content/blob0.bundle"), default, 0, m, default, true, default)).ToArray(), null);

    private IMergeEngine _engine = null!;

    private async Task<Diagnostic[]> Run(
        MergeReportEntry[] entries, MergedOutput[]? outputs = null, ContestedPath[]? notContested = null, AMergeGame? game = null, bool engineless = false, ILogger<EngineMergeDiagnosticsEmitter>? logger = null)
    {
        var engine = _engine = Substitute.For<IMergeEngine>();
        engine.Serves(Arg.Any<IGameData>()).Returns(!engineless);
        engine.PlanAndReport(Arg.Any<Loadout.ReadOnly>(), Arg.Any<MergeSourceMode>()).Returns(new MergePlanReport(new MergeReport(entries, null, "hint"), outputs ?? []));
        engine.NotContested(Arg.Any<Loadout.ReadOnly>()).Returns(notContested ?? []);

        var emitter = new EngineMergeDiagnosticsEmitter(new MergeEngines([engine]), logger ?? NullLogger<EngineMergeDiagnosticsEmitter>.Instance, _ => game ?? new TestMergeGame());
        var loadout = Refresh(await CreateLoadout());
        return await Collect(emitter.Diagnose(loadout, CancellationToken.None));
    }

    private static async Task<Diagnostic[]> Collect(IAsyncEnumerable<Diagnostic> cards)
    {
        var result = new List<Diagnostic>();
        await foreach (var card in cards) result.Add(card);
        return result.ToArray();
    }

    private static string Summary(Diagnostic card) => card.FormatSummary(new PlainDiagnosticWriter());
    private static string Details(Diagnostic card) => card.FormatDetails(new PlainDiagnosticWriter());

    [Fact]
    public async Task EveryLossIsOneWarningNamingBothModsTheLinesAndThePageToReorderOn()
    {
        var cards = await Run([Merged()], [Output(Loss)]);

        var card = cards.Should().ContainSingle().Which;
        card.Severity.Should().Be(DiagnosticSeverity.Warning);
        Summary(card).Should().Contain("Beta's change to `scripts/x.ws` lines 4–7").And.Contain("Alpha's change is used");
        Details(card).Should().Contain("move Beta below Alpha so it loads after it").And.Contain("Change it on the Test Order page.");
    }

    [Fact]
    public async Task ANotMergeablePathIsOneWarningPerLosingCopyNamingTheWinner()
    {
        var cards = await Run([NotMergeable("content/foo.xml", "Alpha", "Beta", "Gamma")]);

        cards.Should().HaveCount(2).And.OnlyContain(c => c.Severity == DiagnosticSeverity.Warning);
        Summary(cards[0]).Should().Contain("Beta's copy of content/foo.xml").And.Contain("Alpha's copy wins").And.Contain("could not be merged (not a file the app merges; load order decides)");
        Summary(cards[1]).Should().Contain("Gamma's copy of content/foo.xml").And.Contain("Alpha's copy wins");
        Details(cards[0]).Should().EndWith("Change it on the Test Order page.");
        cards.Select(Summary).Should().NotContain(s => s.Contains("Alpha's copy of"), "the winner's copy is used");
    }

    [Fact]
    public async Task APathTheGameLoadsPerModIsASuggestionThatSaysWhichCopyWinsAndNamesEachModOnce()
    {
        var cards = await Run([], notContested: [Bundle("Alpha", "Beta")]);

        var card = cards.Should().ContainSingle().Which;
        card.Severity.Should().Be(DiagnosticSeverity.Suggestion);
        var summary = Summary(card);
        summary.Should().Contain("content/blob0.bundle ships in Alpha, Beta").And.Contain("the higher-priority mod's copy is used");
        summary.Should().NotContain("not used", "nothing is left out, so it is not the dropped-item wording");
        summary.Split("Alpha").Length.Should().Be(2, "each mod is named once");
        Details(card).Should().Contain("Test Order page").And.NotContain("left out");
    }

    [Fact]
    public async Task SeveralHunksLostToTheSameModAreOneWarningListingEveryRange()
    {
        var other = new LinesKept(Script, 20, 21, "Gamma", "Alpha");
        var cards = await Run([Merged()], [Output(Loss, new LinesKept(Script, 12, 15, "Alpha", "Beta"), other)]);

        cards.Should().HaveCount(2).And.OnlyContain(c => c.Severity == DiagnosticSeverity.Warning);
        Summary(cards[0]).Should().Contain("Beta's changes to `scripts/x.ws` lines 4–7, 12–15");
        Summary(cards[1]).Should().Contain("Alpha's change to `scripts/x.ws` lines 20–21");
    }

    [Fact]
    public async Task ANotMergeablePathWithOneModOnlyStillGetsACard()
    {
        var cards = await Run([NotMergeable("content/foo.xml", "Alpha")]);

        Summary(cards.Should().ContainSingle().Which).Should().Contain("content/foo.xml").And.Contain("Alpha ships it more than once");
    }

    [Fact]
    public async Task ManyUntrustedOriginalsAreOneSuggestionNamingTheFirstFewPaths()
    {
        var notes = Enumerable.Range(1, 7).Select(i => new VanillaBaseUntrusted($"scripts/{i}.ws", "edited after install")).ToArray<IMergeNote>();

        var cards = await Run([Merged()], [Output(notes)]);

        var card = cards.Should().ContainSingle().Which;
        Summary(card).Should().Contain("scripts/1.ws").And.Contain("scripts/5.ws").And.NotContain("scripts/6.ws").And.Contain("and 2 more")
            .And.Contain("the higher-priority mod's version wins");
    }

    [Fact]
    public async Task TheEngineIsAskedOnceWhateverTheCards()
    {
        await Run([Merged(), NotMergeable("content/foo.xml", "Alpha", "Beta")],
            [Output(Loss, new VanillaBaseUntrusted(Script, "edited after install"))], [Bundle("Alpha", "Beta")]);

        _engine.Received(1).PlanAndReport(Arg.Any<Loadout.ReadOnly>(), Arg.Any<MergeSourceMode>());
        _engine.DidNotReceiveWithAnyArgs().Report(default, default);
        _engine.DidNotReceiveWithAnyArgs().Plan(default, default);
        _engine.Received(1).NotContested(Arg.Any<Loadout.ReadOnly>());
    }

    [Fact]
    public async Task AReportEntryWithoutAnOutputIsLoggedAsAnErrorAndTheOtherCardsStillAppear()
    {
        var logger = Substitute.For<ILogger<EngineMergeDiagnosticsEmitter>>();

        var cards = await Run([Merged("scripts/lost.ws"), NotMergeable("content/foo.xml", "Alpha", "Beta")], [], logger: logger);

        cards.Should().ContainSingle("the not-mergeable path still gets its card");
        logger.ReceivedCalls().Should().Contain(c => c.GetMethodInfo().Name == "Log" && (LogLevel)c.GetArguments()[0]! == LogLevel.Error);
    }

    [Fact]
    public async Task AnUntrustedOriginalCopyIsASuggestionAndAMissingOneRaisesNothing()
    {
        var cards = await Run([Merged()], [Output(new NoVanillaBase(Script), new VanillaBaseUntrusted("scripts/y.ws", "edited after install"))]);

        var card = cards.Should().ContainSingle().Which;
        card.Severity.Should().Be(DiagnosticSeverity.Suggestion);
        Summary(card).Should().Contain("scripts/y.ws").And.Contain("edited after install").And.Contain("the higher-priority mod's version wins");
        Details(card).Should().Contain("Test Order page");
    }

    [Fact]
    public async Task ANoteThatIsNeitherALossNorAVanillaNoteRaisesNoCard()
    {
        var cards = await Run([Merged()], [Output(new Detail())]);

        cards.Should().BeEmpty();
    }

    private sealed record Detail : IMergeDetailNote
    {
        public string Describe() => "took the whole file from Alpha";
    }

    [Fact]
    public async Task TheTwoDiagnoseOverloadsAgree()
    {
        var engine = Substitute.For<IMergeEngine>();
        engine.Serves(Arg.Any<IGameData>()).Returns(true);
        engine.PlanAndReport(Arg.Any<Loadout.ReadOnly>(), Arg.Any<MergeSourceMode>()).Returns(
            new MergePlanReport(new MergeReport([Merged(), NotMergeable("content/foo.xml", "Alpha", "Beta")], null, "hint"), [Output(Loss)]));
        engine.NotContested(Arg.Any<Loadout.ReadOnly>()).Returns([Bundle("Alpha", "Beta")]);
        var emitter = new EngineMergeDiagnosticsEmitter(new MergeEngines([engine]), NullLogger<EngineMergeDiagnosticsEmitter>.Instance, _ => new TestMergeGame());
        var loadout = Refresh(await CreateLoadout());

        var plain = await Collect(emitter.Diagnose(loadout, CancellationToken.None));
        var withTree = await Collect(((ILoadoutDiagnosticEmitter)emitter).Diagnose(loadout, FrozenDictionary<GamePath, SyncNode>.Empty, CancellationToken.None));

        plain.Should().HaveCount(3);
        withTree.Select(c => (c.Id, Summary(c))).Should().Equal(plain.Select(c => (c.Id, Summary(c))));
    }

    [Fact]
    public async Task AGameWithoutAnEngineYieldsNothing()
    {
        var cards = await Run([Merged()], [Output(Loss)], engineless: true);

        cards.Should().BeEmpty();
    }

    private sealed class Trusting : IVanillaTrust
    {
        public bool IsTrusted(GameInstallation installation, GamePath path, MultiHash currentHashes, out string reason)
        {
            reason = "";
            return true;
        }
    }

    /// <summary>A loadout where two mods rewrite line 2 of their own rel, run through the real engine.</summary>
    private async Task<(EngineMergeDiagnosticsEmitter Emitter, CountingMergeGame Game, Loadout.ReadOnly Loadout)> RealEngine(string first, string second)
    {
        var loadout = await CreateLoadout();
        var rel = $"scripts/cards-{Guid.NewGuid():N}.txt";
        var vanilla = GameInstallation.Locations[LocationId.Game].Path.Combine($"vanilla/{rel}");
        vanilla.Parent.CreateDirectory();
        await vanilla.WriteAllTextAsync("a\nb\nc\n");
        await RealMod(loadout, first, "modA", 2, rel, "a\nB1\nc\n");
        await RealMod(loadout, second, "modB", 1, rel, "a\nB2\nc\n");
        var game = new CountingMergeGame();
        var engine = new MergeEngine(FileStore, new Trusting(), NullLogger<MergeEngine>.Instance, _ => game);
        return (new EngineMergeDiagnosticsEmitter(new MergeEngines([new ServingEveryGame(engine)]), NullLogger<EngineMergeDiagnosticsEmitter>.Instance, _ => game), game, Refresh(loadout));
    }

    private async Task RealMod(Loadout.ReadOnly loadout, string name, string package, ulong priority, string rel, string text)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, name);
        var path = new GamePath(LocationId.Game, $"Mods/{package}/{rel}");
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        AddFile(tx, loadout.LoadoutId, group, path, bytes, out var hash, out var size);
        await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(path.Path, new MemoryStream(bytes)), hash, size)]);
        _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = group, Priority = ConflictPriority.From(priority) };
        await tx.Commit();
    }

    [Fact]
    public async Task TheReportAndThePlanAreReadFromOneScanOfTheLoadout()
    {
        var (emitter, game, loadout) = await RealEngine("Alpha", "Beta");

        var cards = await Collect(emitter.Diagnose(loadout, CancellationToken.None));

        cards.Should().ContainSingle();
        game.MergesCalls.Should().Be(1, "the report and the plan are two views of one plan, so the loadout is scanned for contests once");
    }

    [Fact]
    public async Task TwoModsWithTheSameNameGetACardThatTellsThemApart()
    {
        var (emitter, _, loadout) = await RealEngine("Foo", "Foo");

        var card = (await Collect(emitter.Diagnose(loadout, CancellationToken.None))).Should().ContainSingle().Which;

        Summary(card).Should().Contain("Foo (modB)'s change to").And.Contain("Foo (modA)'s change is used");
        Details(card).Should().Contain("move Foo (modB) below Foo (modA) so it loads after it");
    }

    /// <summary>A plan that fails: the base yields the failure card and nothing else.</summary>
    private sealed class FailingEmitter : AMergeDiagnosticsEmitter<MergeReport>
    {
        public FailingEmitter() : base(NullLogger.Instance) { }
        protected override string ReorderPage(Loadout.ReadOnly loadout) => "File Conflicts";
        protected override MergeReport? TryPlan(Loadout.ReadOnly loadout, out Diagnostic? failure)
        {
            failure = Card(new DroppedItem("it", "plan", "it failed", "Load Order"));
            return null;
        }
        protected override IEnumerable<MergedEntry> Merged(Loadout.ReadOnly loadout, MergeReport plan) => throw new InvalidOperationException();
    }

    [Fact]
    public async Task APlanThatFailsYieldsOnlyItsFailureCard()
    {
        var loadout = Refresh(await CreateLoadout());

        var cards = await Collect(new FailingEmitter().Diagnose(loadout, CancellationToken.None));

        Summary(cards.Should().ContainSingle().Which).Should().Contain("it from plan is not used: it failed");
    }

    /// <summary>Cards that are not dropped items come before the note cards.</summary>
    private sealed class ExtraFirstEmitter : AMergeDiagnosticsEmitter<MergeReport>
    {
        public ExtraFirstEmitter() : base(NullLogger.Instance) { }
        protected override string ReorderPage(Loadout.ReadOnly loadout) => "File Conflicts";
        protected override MergeReport? TryPlan(Loadout.ReadOnly loadout, out Diagnostic? failure)
        {
            failure = null;
            return new MergeReport([], null, "hint");
        }
        protected override IEnumerable<MergedEntry> Merged(Loadout.ReadOnly loadout, MergeReport plan) => [new MergedEntry(Script, ["Alpha", "Beta"], [Loss])];
        protected override IEnumerable<Diagnostic> Extra(Loadout.ReadOnly loadout, MergeReport plan) => [Card(new DroppedItem("extra", "x", "r", "File Conflicts"))];
    }

    [Fact]
    public async Task CardsThatAreNotDroppedItemsComeBeforeTheNoteCards()
    {
        var loadout = Refresh(await CreateLoadout());

        var cards = await Collect(new ExtraFirstEmitter().Diagnose(loadout, CancellationToken.None));

        cards.Select(Summary).Should().HaveCount(2);
        Summary(cards[0]).Should().StartWith("extra");
        Summary(cards[1]).Should().Contain("Beta's change");
    }

    /// <summary>EngineMergeDiagnosticsEmitter is sealed, so this stands in for it to reach <c>Dropped</c>.</summary>
    private sealed class FixtureEmitter : AMergeDiagnosticsEmitter<MergeReport>
    {
        public FixtureEmitter() : base(NullLogger.Instance) { }
        protected override string ReorderPage(Loadout.ReadOnly loadout) => "File Conflicts";
        protected override MergeReport? TryPlan(Loadout.ReadOnly loadout, out Diagnostic? failure)
        {
            failure = null;
            return new MergeReport([EngineMergeDiagnosticsEmitterTests.Merged()], null, "hint");
        }
        protected override IEnumerable<MergedEntry> Merged(Loadout.ReadOnly loadout, MergeReport plan)
            => [new MergedEntry(Script, ["Alpha", "Beta"], [Loss, new NoVanillaBase(Script)])];

        public IAsyncEnumerable<DroppedItem> Items(Loadout.ReadOnly loadout) => Dropped(loadout, FrozenDictionary<GamePath, SyncNode>.Empty, CancellationToken.None);
    }

    [Fact]
    public async Task DroppedListsTheSameLosses()
    {
        var loadout = Refresh(await CreateLoadout());
        var items = new List<DroppedItem>();
        await foreach (var item in new FixtureEmitter().Items(loadout)) items.Add(item);

        var dropped = items.Should().ContainSingle().Which;
        dropped.Item.Should().Be("Beta's change to `scripts/x.ws` lines 4–7");
        dropped.Source.Should().Be("Beta");
        dropped.Page.Should().Be("File Conflicts");
        dropped.Severity.Should().Be(DiagnosticSeverity.Warning);
    }
}
