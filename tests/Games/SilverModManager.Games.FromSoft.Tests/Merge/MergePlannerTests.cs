using System.Text;
using FluentAssertions;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;
using NexusMods.Hashing.xxHash3;
using FormatEsd = SilverModManager.Games.FromSoft.Formats.Esd;
using FormatRegulation = SilverModManager.Games.FromSoft.Formats.Regulation;
using FormatTpf = SilverModManager.Games.FromSoft.Formats.Tpf;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

/// <summary>
/// Ports of the erm resolve tests that still apply when the merge is automatic: test_resolve_is_atomic_on_a_later_path_failure,
/// test_a_supplied_base_leads_the_fold, test_only_the_fold_that_leads_with_the_supplied_base_is_told_so,
/// test_no_fold_is_told_its_base_is_the_game_without_a_supplied_base, test_a_base_for_another_path_does_not_leak_into_this_merge,
/// test_a_two_way_strategy_still_takes_two_arguments, test_resolve_folds_a_merge_across_three_providers,
/// test_a_three_way_strategy_gets_vanilla_on_every_fold_step, test_a_three_way_merge_without_a_vanilla_source_raises,
/// test_a_strategy_needing_notes_gets_its_note_back_tagged_with_the_path, test_resolve_without_a_notes_list_does_not_raise,
/// test_a_strategy_not_needing_notes_is_still_called_with_two_or_three_arguments,
/// test_a_two_way_strategy_in_needs_notes_still_gets_a_notes_list,
/// test_resolve_drives_the_real_esd_three_way_strategy_and_returns_its_note, test_a_base_for_a_path_nobody_provides_merges_nothing.
/// </summary>
public class MergePlannerTests
{
    private static readonly FromSoftFormatTraits Traits = MergeStrategiesTests.WithRegulation("regulation.test");
    private static readonly IReadOnlySet<string> NoVanilla = new HashSet<string>();

    private static ProviderFile File(string package, string rel, ulong priority) => new(package, rel, Hash.From((ulong)(package.GetHashCode() ^ rel.GetHashCode())), priority, package);

    private static byte[] B(string text) => Encoding.UTF8.GetBytes(text);

    private static string S(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);

    /// <summary>What a fake fold was handed on one step.</summary>
    private sealed record Call(string Accumulated, string Next, string? Vanilla, bool BaseIsGame, string AccumulatedName, string NextName, bool HadNotes);

    /// <summary>A fold that concatenates its two sides and records what it was given.</summary>
    private static MergeFold Recording(List<Call> calls, Action<ICollection<IMergeNote>?>? withNotes = null) =>
        (acc, next, vanilla, hasVanilla, baseIsGame, accName, nextName, notes, _) =>
        {
            calls.Add(new Call(S(acc), S(next), hasVanilla ? S(vanilla) : null, baseIsGame, accName, nextName, notes is not null));
            withNotes?.Invoke(notes);
            return B(S(acc) + "+" + S(next));
        };

    private static MergePathIndex Index(IReadOnlySet<string>? vanilla, params ProviderFile[] files) => MergePathIndex.Build(files, Traits, vanilla ?? NoVanilla);

    private static Func<ProviderFile, byte[]> ByPackage => f => B(f.PackageId);

    private static Func<string, IReadOnlyList<ProviderFile>, byte[]?> Vanilla(string text) => (_, _) => B(text);

    private static readonly Func<string, byte[]> NoBase = path => throw new InvalidOperationException($"asked for the game's {path}");

    private static MergePlan Plan(MergePathIndex index, MergeFold fold, Func<ProviderFile, byte[]>? bytes = null,
        Func<string, IReadOnlyList<ProviderFile>, byte[]?>? vanilla = null, Func<string, byte[]>? gameBase = null)
        => MergePlanner.Plan(index, bytes ?? ByPackage, vanilla ?? Vanilla("van"), gameBase ?? NoBase, Traits, _ => fold);

    [Fact]
    public void A_later_path_failing_leaves_no_output_for_the_earlier_one()
    {
        var index = Index(null,
            File("a", "a.tpf.dcx", 2), File("b", "a.tpf.dcx", 1),
            File("a", "b.tpf.dcx", 2), File("b", "b.tpf.dcx", 1));
        var calls = 0;
        MergeFold fold = (_, _, _, _, _, _, _, _, _) =>
            calls++ == 0 ? B("ok") : throw new MergeException("cannot join");

        var plan = Plan(index, fold);

        plan.Merged.Should().BeEmpty();
        plan.Refusal.Should().NotBeNull();
        plan.Refusal!.Path.Should().Be("b.tpf.dcx");
        plan.Refusal.Strategy.Should().Be(MergeStrategy.TpfUnion);
        plan.Refusal.Reason.Should().Be("cannot join");
    }

    [Fact]
    public void A_supplied_base_leads_the_fold()
    {
        var index = Index(null, File("a", "regulation.test", 2), File("b", "regulation.test", 1));
        var recorded = new List<Call>();

        var plan = Plan(index, Recording(recorded), gameBase: _ => B("game"));

        recorded.Select(c => (c.Accumulated, c.Next)).Should().Equal(("game", "a"), ("game+a", "b"));
        S(plan.Merged.Single().Bytes).Should().Be("game+a+b");
    }

    [Fact]
    public void Only_the_fold_that_leads_with_the_supplied_base_is_told_so()
    {
        var index = Index(null, File("a", "regulation.test", 3), File("b", "regulation.test", 2), File("c", "regulation.test", 1));
        var recorded = new List<Call>();

        Plan(index, Recording(recorded), gameBase: _ => B("game"));

        recorded.Select(c => c.BaseIsGame).Should().Equal(true, false, false);
        recorded.Select(c => c.AccumulatedName).Should().Equal("the game", "a", "a");
        recorded.Select(c => c.NextName).Should().Equal("a", "b", "c");
    }

    [Fact]
    public void No_fold_is_told_its_base_is_the_game_without_a_supplied_base()
    {
        var index = Index(null, File("a", "one.tpf.dcx", 3), File("b", "one.tpf.dcx", 2), File("c", "one.tpf.dcx", 1));
        var recorded = new List<Call>();

        Plan(index, Recording(recorded));

        recorded.Should().HaveCount(2).And.OnlyContain(c => !c.BaseIsGame);
    }

    [Fact]
    public void A_base_for_another_path_does_not_leak_into_this_merge()
    {
        // Only the regulation starts from the game's file; the base callback must never be asked about a tpf.
        var index = Index(null,
            File("a", "regulation.test", 2), File("b", "regulation.test", 1),
            File("a", "x.tpf.dcx", 2), File("b", "x.tpf.dcx", 1));
        var recorded = new Dictionary<MergeStrategy, List<Call>> { [MergeStrategy.ParamRows] = [], [MergeStrategy.TpfUnion] = [] };

        MergePlanner.Plan(index, ByPackage, Vanilla("van"), path => path == "regulation.test" ? B("game") : throw new InvalidOperationException(path), Traits,
            strategy => Recording(recorded[strategy]));

        recorded[MergeStrategy.TpfUnion].Single().Accumulated.Should().Be("a");
        recorded[MergeStrategy.ParamRows].First().Accumulated.Should().Be("game");
    }

    [Fact]
    public void A_two_way_strategy_is_told_there_is_no_vanilla_and_never_asks_for_one()
    {
        var index = Index(null, File("a", "one.tpf.dcx", 2), File("b", "one.tpf.dcx", 1));
        var recorded = new List<Call>();

        Plan(index, Recording(recorded), vanilla: (_, _) => throw new InvalidOperationException("no vanilla wanted"));

        recorded.Single().Vanilla.Should().BeNull();
    }

    [Fact]
    public void A_merge_across_three_providers_folds_the_highest_priority_first()
    {
        var index = Index(null, File("low", "one.tpf.dcx", 1), File("high", "one.tpf.dcx", 3), File("mid", "one.tpf.dcx", 2));
        var recorded = new List<Call>();

        var plan = Plan(index, Recording(recorded));

        S(plan.Merged.Single().Bytes).Should().Be("high+mid+low");
        plan.Merged.Single().Contributors.Select(c => c.PackageId).Should().Equal("high", "mid", "low");
    }

    [Fact]
    public void A_three_way_strategy_gets_vanilla_on_every_fold_step()
    {
        var index = Index(new HashSet<string> { "t.msgbnd.dcx" }, File("a", "t.msgbnd.dcx", 3), File("b", "t.msgbnd.dcx", 2), File("c", "t.msgbnd.dcx", 1));
        var recorded = new List<Call>();
        var asked = 0;

        Plan(index, Recording(recorded), vanilla: (_, _) => { asked++; return B("van"); });

        recorded.Select(c => c.Vanilla).Should().Equal("van", "van");
        asked.Should().Be(1, "the ancestor is resolved once per path");
    }

    [Fact]
    public void A_three_way_merge_without_a_vanilla_source_is_refused()
    {
        var index = Index(new HashSet<string> { "t.msgbnd.dcx" }, File("a", "t.msgbnd.dcx", 2), File("b", "t.msgbnd.dcx", 1));

        var plan = Plan(index, Recording([]), vanilla: (_, _) => null);

        plan.Merged.Should().BeEmpty();
        plan.Refusal.Should().Be(new MergeRefusal("t.msgbnd.dcx", MergeStrategy.FmgThreeWay,
            "compares both mods against the vanilla file they branched from, and none is available"));
    }

    [Fact]
    public void A_strategys_notes_come_back_tagged_with_the_path()
    {
        var index = Index(null, File("a", "one.tpf.dcx", 2), File("b", "one.tpf.dcx", 1), File("a", "two.tpf.dcx", 2), File("b", "two.tpf.dcx", 1));
        var fold = Recording([], notes => notes!.Add(new TextureKept("icon", "a", "b")));

        var plan = Plan(index, fold);

        plan.Notes.Should().BeEquivalentTo([
            new PathNote("one.tpf.dcx", new TextureKept("icon", "a", "b")),
            new PathNote("two.tpf.dcx", new TextureKept("icon", "a", "b"))]);
        plan.Merged.Select(m => m.Notes.Count).Should().Equal(1, 1);
    }

    [Fact]
    public void A_strategy_that_reports_nothing_still_gets_a_notes_list_and_the_plan_has_none()
    {
        var index = Index(null, File("a", "one.tpf.dcx", 2), File("b", "one.tpf.dcx", 1));
        var recorded = new List<Call>();

        var plan = Plan(index, Recording(recorded));

        recorded.Single().HadNotes.Should().BeTrue();
        plan.Notes.Should().BeEmpty();
        plan.Refusal.Should().BeNull();
    }

    [Fact]
    public void A_base_for_a_path_nobody_provides_merges_nothing()
    {
        var index = Index(null, File("a", "only.tpf.dcx", 1), File("a", "other.txt", 1));

        var plan = MergePlanner.Plan(index, ByPackage, Vanilla("van"), path => B("game of " + path), Traits, _ => throw new InvalidOperationException("nothing to fold"));

        plan.Merged.Should().BeEmpty();
        plan.Refusal.Should().BeNull();
    }

    [Fact]
    public void A_path_the_index_will_not_merge_passes_through_and_merges_nothing()
    {
        // Two mods, one file, no vanilla dump on offer: the index marks it not mergeable.
        var index = Index(null, File("a", "t.talkesdbnd.dcx", 2), File("b", "t.talkesdbnd.dcx", 1));

        var plan = Plan(index, Recording([]));

        plan.Merged.Should().BeEmpty();
        plan.Refusal.Should().BeNull();
        plan.NotMergeable.Should().ContainSingle().Which.Should().BeSameAs(index.Candidates.Single());
    }

    [Fact]
    public void Every_strategy_has_a_binding()
    {
        foreach (var strategy in Enum.GetValues<MergeStrategy>())
            MergeStrategyBindings.For(strategy).Should().NotBeNull($"{strategy} must be performed by something");
    }

    [Fact]
    public void A_three_way_binding_called_without_a_vanilla_throws_rather_than_comparing_against_nothing()
    {
        foreach (var strategy in Enum.GetValues<MergeStrategy>().Where(MergeStrategies.NeedsVanilla))
        {
            var act = () => MergeStrategyBindings.For(strategy)([], [], [], false, false, "a", "b", null, Traits);
            act.Should().Throw<MergeException>(strategy.ToString()).WithMessage("*needs the vanilla*");
        }
    }

    // --- the real strategies ---

    private static FromSoftFormatTraits ER => EldenRingGame.Definition.Formats;

    private static ProviderFile Real(string package, string rel, ulong priority) => File(package, rel, priority);

    private static MergePlan RealPlan(IReadOnlyDictionary<string, byte[]> contents, IReadOnlySet<string> vanillaPaths,
        Func<string, byte[]?> vanilla, Func<string, byte[]> gameBase, params ProviderFile[] files)
    {
        var index = MergePathIndex.Build(files, ER, vanillaPaths);
        return MergePlanner.Plan(index, f => contents[f.PackageId + ":" + f.RelPath], (path, _) => vanilla(path), gameBase, ER);
    }

    private static byte[] Msgbnd(params (int Entry, (int Id, string? Text)[] Rows)[] tables) =>
        MsgbndFixture.Build(tables.ToDictionary(t => t.Entry, t => (IReadOnlyDictionary<int, string?>)t.Rows.ToDictionary(r => r.Id, r => r.Text)), ER);

    [Fact]
    public void Two_message_archives_union_with_the_higher_priority_mods_text_winning_a_shared_id()
    {
        const string path = "msg/engus/item.msgbnd.dcx";
        var contents = new Dictionary<string, byte[]>
        {
            ["high:" + path] = Msgbnd((1, [(100, "high's text"), (101, "only high")])),
            ["low:" + path] = Msgbnd((1, [(100, "low's text"), (200, "only low")])),
        };

        var plan = RealPlan(contents, NoVanilla, _ => null, NoBase, Real("low", path, 1), Real("high", path, 2));

        plan.Refusal.Should().BeNull();
        var merged = plan.Merged.Single();
        merged.Strategy.Should().Be(MergeStrategy.FmgUnion);
        merged.VanillaSource.Should().BeEmpty();
        MsgbndFixture.ReadBack(merged.Bytes, ER)[1].Should().Equal(new Dictionary<int, string?> { [100] = "high's text", [101] = "only high", [200] = "only low" });
        plan.Notes.Should().ContainSingle().Which.Should().Be(new PathNote(path, new TextKept(1, 100, "high", "low")));
    }

    [Fact]
    public void Two_texture_archives_union()
    {
        const string path = "menu/hi/01_common.tpf.dcx";
        byte[] Tpf(params (string Name, string Data)[] textures) =>
            Dcx.Write(FormatTpf.Write(new TpfArchive(
                [.. textures.Select(t => new Texture(t.Name, Encoding.ASCII.GetBytes(t.Data), 102, 0, 1, 0))], Platform: 0, Flag2: 3, Encoding: 1)), ER.PackedWrite);
        var contents = new Dictionary<string, byte[]>
        {
            ["high:" + path] = Tpf(("A", "high-a")),
            ["low:" + path] = Tpf(("A", "low-a"), ("B", "low-b")),
        };

        var plan = RealPlan(contents, NoVanilla, _ => null, NoBase, Real("low", path, 1), Real("high", path, 2));

        var names = FormatTpf.Read(Dcx.Read(plan.Merged.Single().Bytes, ER)).Textures.ToDictionary(t => t.Name, t => Encoding.ASCII.GetString(t.Data));
        names.Should().Equal(new Dictionary<string, string> { ["A"] = "high-a", ["B"] = "low-b" });
        plan.Notes.Single().Note.Should().Be(new TextureKept("A", "high", "low"));
    }

    private const string SpType = "SP_EFFECT_PARAM_ST";

    private static byte[] Regulation(params (int Id, byte Fill)[] rows)
    {
        var ordered = rows.OrderBy(r => r.Id).ToArray();
        var next = 0;
        var blob = ParamFixture.Make(ordered.Select(r => r.Id).ToArray(), 8, SpType, fill: _ => Enumerable.Repeat(ordered[next++].Fill, 8).ToArray());
        return RegulationFixture.BuildFromEntries([(1, $"{SpType}.param", blob)], "07D7R6\0\0", ER);
    }

    private static Dictionary<int, byte[]> RowsOf(byte[] regulation) =>
        Param.Read(FormatRegulation.Entries(regulation, ER).Single().Data).Rows.ToDictionary(r => r.Id, r => r.Data);

    [Fact]
    public void Two_regulations_fold_onto_the_game_file_and_name_their_row_notes_by_path()
    {
        const string path = "regulation.bin";
        var vanilla = Regulation((1, 0), (2, 0), (3, 0));
        var contents = new Dictionary<string, byte[]>
        {
            ["high:" + path] = Regulation((1, 0xaa), (2, 0), (3, 0xdd)),
            ["low:" + path] = Regulation((1, 0xbb), (2, 0xcc), (3, 0)),
        };

        var plan = RealPlan(contents, NoVanilla, _ => vanilla, _ => vanilla, Real("low", path, 1), Real("high", path, 2));

        plan.Refusal.Should().BeNull();
        var rows = RowsOf(plan.Merged.Single().Bytes);
        rows[1].Should().Equal(Enumerable.Repeat((byte)0xaa, 8), "both changed row 1; the higher-priority mod keeps it");
        rows[2].Should().Equal(Enumerable.Repeat((byte)0xcc, 8));
        rows[3].Should().Equal(Enumerable.Repeat((byte)0xdd, 8));
        plan.Notes.Should().Contain(new PathNote(path, new RowConflict(1, 1, "high", "low")));
        plan.Merged.Single().VanillaSource.Should().NotBeEmpty();
    }

    [Fact]
    public void A_regulation_with_no_vanilla_is_refused_and_names_the_path()
    {
        const string path = "regulation.bin";
        var reg = Regulation((1, 0));
        var contents = new Dictionary<string, byte[]> { ["high:" + path] = reg, ["low:" + path] = reg };

        var plan = RealPlan(contents, NoVanilla, _ => null, _ => reg, Real("low", path, 1), Real("high", path, 2));

        plan.Merged.Should().BeEmpty();
        plan.Refusal.Should().NotBeNull();
        plan.Refusal!.Path.Should().Be(path);
        plan.Refusal.Strategy.Should().Be(MergeStrategy.ParamRows);
        plan.Refusal.Reason.Should().Contain("none is available");
    }

    [Fact]
    public void A_not_mergeable_candidate_passes_through_untouched()
    {
        const string path = "events/a.unknown";
        var contents = new Dictionary<string, byte[]> { ["a:" + path] = [1], ["b:" + path] = [2] };

        var plan = RealPlan(contents, NoVanilla, _ => null, NoBase, Real("a", path, 2), Real("b", path, 1));

        plan.Merged.Should().BeEmpty();
        plan.Refusal.Should().BeNull();
        plan.NotMergeable.Single().Outcome.Reason.Should().Be(MergeOutcome.NoStrategy);
    }

    [Fact]
    public void The_real_talk_archive_strategy_runs_and_a_failure_in_it_is_a_refusal()
    {
        // Archives holding different entries are the one thing the talk merge refuses on before reading any machine.
        const string path = "script/talk/m00.talkesdbnd.dcx";
        var van = EsdMergeFixtureArchive(ER, [0, 1]);
        var contents = new Dictionary<string, byte[]>
        {
            ["high:" + path] = EsdMergeFixtureArchive(ER, [0, 1]),
            ["low:" + path] = EsdMergeFixtureArchive(ER, [0]),
        };

        var plan = RealPlan(contents, new HashSet<string> { path }, _ => van, NoBase, Real("low", path, 1), Real("high", path, 2));

        plan.Merged.Should().BeEmpty();
        plan.Refusal.Should().NotBeNull();
        plan.Refusal!.Strategy.Should().Be(MergeStrategy.EsdThreeWay);
    }

    [Fact]
    public void The_real_talk_archive_strategy_merges_when_the_archives_agree()
    {
        const string path = "script/talk/m00.talkesdbnd.dcx";
        var archive = EsdMergeFixtureArchive(ER, [0, 1]);
        var contents = new Dictionary<string, byte[]> { ["high:" + path] = archive, ["low:" + path] = archive };

        var plan = RealPlan(contents, new HashSet<string> { path }, _ => archive, NoBase, Real("low", path, 1), Real("high", path, 2));

        plan.Refusal.Should().BeNull();
        plan.Merged.Single().Strategy.Should().Be(MergeStrategy.EsdThreeWay);
        Binders.For(ER).Read(Dcx.Read(plan.Merged.Single().Bytes, ER)).Select(e => e.Id).Should().Equal(0, 1);
    }

    // --- golden: erm's resolve over the same packages ---

    private static byte[] Golden(string name) => Formats.GoldenFilesTests.Read($"planner_{name}");

    [Fact]
    public void The_plan_matches_what_erms_resolve_writes_for_the_same_packages()
    {
        const string reg = "regulation.bin";
        const string msg = "msg/engus/item.msgbnd.dcx";
        const string tpf = "menu/hi/01_common.tpf.dcx";
        var contents = new Dictionary<string, byte[]>
        {
            ["high:" + reg] = Golden("high_regulation.bin"), ["low:" + reg] = Golden("low_regulation.bin"),
            ["high:" + msg] = Golden("high_item.msgbnd.dcx"), ["low:" + msg] = Golden("low_item.msgbnd.dcx"),
            ["high:" + tpf] = Golden("high_01_common.tpf.dcx"), ["low:" + tpf] = Golden("low_01_common.tpf.dcx"),
        };

        var plan = RealPlan(contents, NoVanilla, _ => Golden("vanilla_regulation.bin"), _ => Golden("game_regulation.bin"),
            Real("low", reg, 1), Real("high", reg, 2), Real("low", msg, 1), Real("high", msg, 2), Real("low", tpf, 1), Real("high", tpf, 2));

        plan.Refusal.Should().BeNull();
        var byPath = plan.Merged.ToDictionary(m => m.Path, m => m.Bytes);
        byPath[reg].Should().Equal(Golden("merged_regulation.bin"));
        // Decoded: the compressed body of a rewritten archive depends on the zlib in use.
        Dcx.Read(byPath[msg], ER).Should().Equal(Golden("merged_item.msgbnd"));
        Dcx.Read(byPath[tpf], ER).Should().Equal(Golden("merged_01_common.tpf"));
    }

    private static byte[] EsdMergeFixtureArchive(FromSoftFormatTraits traits, int[] ids)
    {
        var blob = FormatEsd.Write(EsdMergeFixture.Archive((1, [(0L, null)])));
        var members = ids.Select(id => (Id: id, Name: $"t{id:D9}.esd", Data: blob)).ToList<(int Id, string Name, byte[] Data)>();
        return Dcx.Write(Bnd4Fixture.Build(members), traits.PackedWrite);
    }
}
