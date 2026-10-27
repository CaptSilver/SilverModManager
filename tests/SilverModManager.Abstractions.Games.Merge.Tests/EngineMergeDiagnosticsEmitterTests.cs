using System.Collections.Frozen;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Hashing.xxHash3;
using NSubstitute;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games.Merge.Tests.Fixtures;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

namespace SilverModManager.Abstractions.Games.Merge.Tests;

public class EngineMergeDiagnosticsEmitterTests(IServiceProvider provider) : AGameTest<StubbedGame>(provider)
{
    private const string Script = "scripts/x.ws";

    /// <summary>The winner is listed above the loser, so moving a mod up is what makes it win.</summary>
    private sealed class HigherWinsGame : TestMergeGame
    {
        public override bool WinnerIsLower => false;
    }

    private static MergeReportEntry Merged(string path = Script) => new(path, "text", ["Alpha", "Beta"], MergeReportOutcome.Merged, [], "the game's own copy", "");

    private static MergeReportEntry NotMergeable(string path, params string[] mods)
        => new(path, "", mods, MergeReportOutcome.NotMergeable, [], "", "not a file the app merges; load order decides");

    private static MergedOutput Output(params IMergeNote[] notes)
        => new(new GamePath(LocationId.Game, $"Mods/merged/{Script}"), Script, [], ["Alpha", "Beta"], notes, "text");

    private static LinesKept Loss => new(Script, 4, 7, "Alpha", "Beta");

    private static ContestedPath Bundle(params string[] mods)
        => new("content/blob0.bundle", mods.Select(m => new ContestedFile(m, "content/blob0.bundle", new GamePath(LocationId.Game, $"Mods/{m}/content/blob0.bundle"), default, 0, m, default, true, default)).ToArray(), null);

    private async Task<Diagnostic[]> Run(
        MergeReportEntry[] entries, MergedOutput[]? outputs = null, ContestedPath[]? notContested = null, AMergeGame? game = null, bool engineless = false)
    {
        var engine = Substitute.For<IMergeEngine>();
        engine.Serves(Arg.Any<IGameData>()).Returns(!engineless);
        engine.Report(Arg.Any<Loadout.ReadOnly>(), Arg.Any<MergeSourceMode>()).Returns(new MergeReport(entries, null, "hint"));
        engine.Plan(Arg.Any<Loadout.ReadOnly>(), Arg.Any<MergeSourceMode>()).Returns(outputs ?? []);
        engine.NotContested(Arg.Any<Loadout.ReadOnly>()).Returns(notContested ?? []);

        var emitter = new EngineMergeDiagnosticsEmitter(new MergeEngines([engine]), NullLogger<EngineMergeDiagnosticsEmitter>.Instance, _ => game ?? new TestMergeGame());
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
        Details(card).Should().Contain("move Beta below Alpha").And.Contain("Change it on the Load Order page.");
    }

    [Fact]
    public async Task AGameWhereTheWinnerIsListedFirstAdvisesMovingTheLoserAbove()
    {
        var cards = await Run([Merged()], [Output(Loss)], game: new HigherWinsGame());

        Details(cards.Should().ContainSingle().Which).Should().Contain("move Beta above Alpha");
    }

    [Fact]
    public async Task ANotMergeablePathIsOneWarningPerLosingCopyNamingTheWinner()
    {
        var cards = await Run([NotMergeable("content/foo.xml", "Alpha", "Beta", "Gamma")]);

        cards.Should().HaveCount(2).And.OnlyContain(c => c.Severity == DiagnosticSeverity.Warning);
        Summary(cards[0]).Should().Contain("Beta's copy of content/foo.xml").And.Contain("Alpha's copy wins").And.Contain("could not be merged (not a file the app merges; load order decides)");
        Summary(cards[1]).Should().Contain("Gamma's copy of content/foo.xml").And.Contain("Alpha's copy wins");
        Details(cards[0]).Should().EndWith("Change it on the Load Order page.");
        cards.Select(Summary).Should().NotContain(s => s.Contains("Alpha's copy of"), "the winner's copy is used");
    }

    [Fact]
    public async Task APathTheGameLoadsPerModIsASuggestionThatSaysWhichCopyWins()
    {
        var cards = await Run([], notContested: [Bundle("Alpha", "Beta")]);

        var card = cards.Should().ContainSingle().Which;
        card.Severity.Should().Be(DiagnosticSeverity.Suggestion);
        Summary(card).Should().Contain("content/blob0.bundle").And.Contain("Alpha, Beta")
            .And.Contain("bundles are not merged, so where they contain the same file the higher-priority mod's copy is used");
        Details(card).Should().Contain("Change it on the Load Order page.");
    }

    [Fact]
    public async Task AnUntrustedOriginalCopyIsASuggestionAndAMissingOneRaisesNothing()
    {
        var cards = await Run([Merged()], [Output(new NoVanillaBase(Script), new VanillaBaseUntrusted("scripts/y.ws", "edited after install"))]);

        var card = cards.Should().ContainSingle().Which;
        card.Severity.Should().Be(DiagnosticSeverity.Suggestion);
        Summary(card).Should().Contain("scripts/y.ws").And.Contain("edited after install");
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
        engine.Report(Arg.Any<Loadout.ReadOnly>(), Arg.Any<MergeSourceMode>()).Returns(new MergeReport([Merged(), NotMergeable("content/foo.xml", "Alpha", "Beta")], null, "hint"));
        engine.Plan(Arg.Any<Loadout.ReadOnly>(), Arg.Any<MergeSourceMode>()).Returns([Output(Loss)]);
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

    /// <summary>A plan that fails: the base yields the failure card and nothing else.</summary>
    private sealed class FailingEmitter : AMergeDiagnosticsEmitter<MergeReport>
    {
        public FailingEmitter() : base(NullLogger.Instance) { }
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
        protected override MergeReport? TryPlan(Loadout.ReadOnly loadout, out Diagnostic? failure)
        {
            failure = null;
            return new MergeReport([], null, "hint");
        }
        protected override IEnumerable<MergedEntry> Merged(Loadout.ReadOnly loadout, MergeReport plan) => [new MergedEntry(Script, "Alpha, Beta", [Loss])];
        protected override IEnumerable<Diagnostic> Extra(Loadout.ReadOnly loadout, MergeReport plan) => [Card(new DroppedItem("extra", "x", "r", "Load Order"))];
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
        protected override MergeReport? TryPlan(Loadout.ReadOnly loadout, out Diagnostic? failure)
        {
            failure = null;
            return new MergeReport([EngineMergeDiagnosticsEmitterTests.Merged()], null, "hint");
        }
        protected override IEnumerable<MergedEntry> Merged(Loadout.ReadOnly loadout, MergeReport plan)
            => [new MergedEntry(Script, "Alpha, Beta", [Loss, new NoVanillaBase(Script)])];

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
        dropped.Page.Should().Be("Load Order");
        dropped.Severity.Should().Be(DiagnosticSeverity.Warning);
    }
}
