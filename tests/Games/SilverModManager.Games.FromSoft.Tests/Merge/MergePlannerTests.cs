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
        (acc, next, vanilla, hasVanilla, baseIsGame, accName, nextName, notes, _, _) =>
        {
            calls.Add(new Call(S(acc), S(next), hasVanilla ? S(vanilla) : null, baseIsGame, accName, nextName, notes is not null));
            withNotes?.Invoke(notes);
            return B(S(acc) + "+" + S(next));
        };

    private static MergePathIndex Index(IReadOnlySet<string>? vanilla, params ProviderFile[] files) => MergePathIndex.Build(files, Traits, vanilla ?? NoVanilla);

    private static Func<ProviderFile, byte[]> ByPackage => f => B(f.PackageId);

    private static Func<string, ProviderFile, byte[]?> Vanilla(string text) => (_, _) => B(text);

    private static readonly Func<string, byte[]> NoBase = path => throw new InvalidOperationException($"asked for the game's {path}");

    private static MergePlan Plan(MergePathIndex index, MergeFold fold, Func<ProviderFile, byte[]>? bytes = null,
        Func<string, ProviderFile, byte[]?>? vanilla = null, Func<string, byte[]>? gameBase = null)
        => MergePlanner.Plan(index, bytes ?? ByPackage, vanilla ?? Vanilla("van"), gameBase ?? NoBase, Traits, _ => fold);

    [Fact]
    public void A_later_regulation_failing_leaves_no_output_for_the_earlier_path()
    {
        var index = Index(null,
            File("a", "a.tpf.dcx", 2), File("b", "a.tpf.dcx", 1),
            File("a", "regulation.test", 2), File("b", "regulation.test", 1));
        var calls = 0;
        MergeFold fold = (_, _, _, _, _, _, _, _, _, _) =>
            calls++ == 0 ? B("ok") : throw new MergeException("cannot join");

        var plan = Plan(index, fold, gameBase: _ => B("game"));

        plan.Merged.Should().BeEmpty();
        plan.Unmerged.Should().BeEmpty();
        plan.Refusal.Should().NotBeNull();
        plan.Refusal!.Path.Should().Be("regulation.test");
        plan.Refusal.Strategy.Should().Be(MergeStrategy.ParamRows);
        plan.Refusal.Reason.Should().Be("cannot join");
    }

    [Fact]
    public void A_failing_merge_of_another_file_falls_back_to_the_priority_winner_without_a_refusal()
    {
        var index = Index(null, File("a", "b.tpf.dcx", 2), File("b", "b.tpf.dcx", 1));
        MergeFold fold = (_, _, _, _, _, _, _, _, _, _) => throw new MergeException("cannot join");

        var plan = Plan(index, fold);

        plan.Refusal.Should().BeNull();
        plan.Merged.Should().BeEmpty();
        var fallback = plan.Unmerged.Should().ContainSingle().Which;
        fallback.Path.Should().Be("b.tpf.dcx");
        fallback.Strategy.Should().Be(MergeStrategy.TpfUnion);
        fallback.Outcome.Merges.Should().BeFalse();
        fallback.Outcome.Reason.Should().Be(MergeOutcome.MergeFailed("cannot join")).And.Contain("cannot join");
        fallback.Winner!.Value.PackageId.Should().Be("a", "loadout priority puts a above b");
    }

    [Fact]
    public void A_path_that_fails_to_merge_does_not_take_the_other_merges_with_it()
    {
        var index = Index(null,
            File("a", "a.tpf.dcx", 2), File("b", "a.tpf.dcx", 1),
            File("a", "b.tpf.dcx", 2), File("b", "b.tpf.dcx", 1));
        var calls = 0;
        MergeFold fold = (_, _, _, _, _, _, _, _, _, _) =>
            calls++ == 0 ? B("ok") : throw new MergeException("cannot join");

        var plan = Plan(index, fold);

        plan.Refusal.Should().BeNull();
        S(plan.Merged.Should().ContainSingle().Which.Bytes).Should().Be("ok");
        plan.Merged.Single().Path.Should().Be("a.tpf.dcx");
        plan.Unmerged.Should().ContainSingle().Which.Path.Should().Be("b.tpf.dcx");
    }

    [Fact]
    public void Case_variant_spellings_merge_into_one_output_under_the_leaders_spelling()
    {
        var index = Index(null, File("a", "Menu/X.tpf.dcx", 1), File("b", "menu/x.tpf.dcx", 2));
        var recorded = new List<Call>();

        var plan = Plan(index, Recording(recorded));

        plan.Refusal.Should().BeNull();
        var merged = plan.Merged.Should().ContainSingle().Subject;
        merged.Path.Should().Be("menu/x.tpf.dcx");
        S(merged.Bytes).Should().Be("b+a");
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
        asked.Should().Be(2, "the ancestor is resolved once for each contributor folded onto the leader");
    }

    [Fact]
    public void A_three_way_merge_without_a_vanilla_source_falls_back_to_the_priority_winner()
    {
        var index = Index(new HashSet<string> { "t.msgbnd.dcx" }, File("a", "t.msgbnd.dcx", 2), File("b", "t.msgbnd.dcx", 1));

        var plan = Plan(index, Recording([]), vanilla: (_, _) => null);

        plan.Merged.Should().BeEmpty();
        plan.Refusal.Should().BeNull();
        plan.Unmerged.Should().ContainSingle().Which.Outcome.Reason
            .Should().Be(MergeOutcome.MergeFailed("b compares against the vanilla file it branched from, and none is available"));
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
            var act = () => MergeStrategyBindings.For(strategy)([], [], [], false, false, "a", "b", null, Traits, null);
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
    public void The_real_talk_archive_strategy_runs_and_a_failure_in_it_falls_back_to_the_priority_winner()
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
        plan.Refusal.Should().BeNull();
        plan.Unmerged.Should().ContainSingle().Which.Strategy.Should().Be(MergeStrategy.EsdThreeWay);
    }

    [Fact]
    public void The_real_partsbnd_strategy_takes_each_mods_own_member()
    {
        const string path = "parts/wp_a_0556.partsbnd.dcx";
        byte[] Bundle(byte[] model, byte[] animations) => PartsbndFixture.Build(ER, [(200, "a.flver", model), (400, "a.anibnd", animations)]);
        var van = Bundle([1, 1, 1], [2, 2, 2]);
        var contents = new Dictionary<string, byte[]>
        {
            ["high:" + path] = Bundle([1, 1, 1], [9, 9, 9]),
            ["low:" + path] = Bundle([7, 7, 7], [2, 2, 2]),
        };

        var plan = RealPlan(contents, new HashSet<string> { path }, _ => van, NoBase, Real("low", path, 1), Real("high", path, 2));

        plan.Refusal.Should().BeNull();
        plan.Merged.Single().Strategy.Should().Be(MergeStrategy.PartsbndThreeWay);
        var members = Binders.For(ER).Read(Dcx.Read(plan.Merged.Single().Bytes, ER)).ToDictionary(e => e.Id, e => e.Data);
        members[200].Should().Equal(7, 7, 7);
        members[400].Should().Equal(9, 9, 9);
    }

    [Fact]
    public void Two_mods_editing_one_equipment_part_differently_merge_into_one_bundle_with_the_members_each_changed()
    {
        const string path = "parts/wp_a_0556.partsbnd.dcx";
        var van = PartsbndMods.Bundle(ER, PartsbndMods.Vanilla());
        var contents = new Dictionary<string, byte[]>
        {
            [PartsbndMods.Moveset + ":" + path] = PartsbndMods.Bundle(ER, PartsbndMods.MovesetMembers()),
            [PartsbndMods.Glow + ":" + path] = PartsbndMods.Bundle(ER, PartsbndMods.GlowMembers()),
        };

        var plan = RealPlan(contents, new HashSet<string> { path }, _ => van, NoBase, Real(PartsbndMods.Moveset, path, 1), Real(PartsbndMods.Glow, path, 2));

        plan.Refusal.Should().BeNull();
        plan.Unmerged.Should().BeEmpty();
        var merged = plan.Merged.Should().ContainSingle().Which;
        merged.Path.Should().Be(path);
        merged.Strategy.Should().Be(MergeStrategy.PartsbndThreeWay);
        var members = PartsbndMods.Read(merged.Bytes, ER);
        members[100].Should().Equal(PartsbndMods.GlowMembers().Single(m => m.Id == 100).Data);
        members[200].Should().Equal(PartsbndMods.GlowMembers().Single(m => m.Id == 200).Data, "both changed the model and Glow has the priority");
        members[201].Should().Equal(PartsbndMods.Vanilla().Single(m => m.Id == 201).Data);
        members[400].Should().Equal(PartsbndMods.MovesetMembers().Single(m => m.Id == 400).Data);
        members[401].Should().Equal(PartsbndMods.MovesetMembers().Single(m => m.Id == 401).Data);
        var kept = merged.Notes.OfType<MemberKept>().Should().ContainSingle().Which;
        (kept.Member, kept.KeptMod, kept.DroppedMod).Should().Be((PartsbndMods.FlverName, PartsbndMods.Glow, PartsbndMods.Moveset));
        kept.Details.Should().Contain("different mods", "the model comes from one mod and the animations from the other");
        merged.Notes.OfType<MemberTaken>().Select(n => (n.Member, n.FromMod)).Should().BeEquivalentTo(
            [(PartsbndMods.AnibndName, PartsbndMods.Moveset), (PartsbndMods.Anibnd1Name, PartsbndMods.Moveset)],
            "Glow leads the fold, so only Moveset's members are takes; the texture archive is the leader's own");
    }

    [Fact]
    public void Bundles_holding_different_members_fall_back_to_the_priority_winner_and_say_why()
    {
        const string path = "parts/wp_a_0556.partsbnd.dcx";
        var van = PartsbndMods.Bundle(ER, PartsbndMods.Vanilla());
        var contents = new Dictionary<string, byte[]>
        {
            [PartsbndMods.Moveset + ":" + path] = PartsbndMods.Bundle(ER, [.. PartsbndMods.MovesetMembers().Where(m => m.Id != 201)]),
            [PartsbndMods.Glow + ":" + path] = PartsbndMods.Bundle(ER, PartsbndMods.GlowMembers()),
        };

        var plan = RealPlan(contents, new HashSet<string> { path }, _ => van, NoBase, Real(PartsbndMods.Moveset, path, 1), Real(PartsbndMods.Glow, path, 2));

        plan.Refusal.Should().BeNull();
        plan.Merged.Should().BeEmpty();
        var unmerged = plan.Unmerged.Should().ContainSingle().Which;
        unmerged.Path.Should().Be(path);
        unmerged.Strategy.Should().Be(MergeStrategy.PartsbndThreeWay);
        unmerged.Outcome.Merges.Should().BeFalse();
        unmerged.Outcome.Reason.Should().StartWith("the merge failed: ").And.Contain("the three bundles hold different entries");
        unmerged.Winner!.Value.PackageId.Should().Be(PartsbndMods.Glow);
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

    // --- the base of an order-sensitive merge ---

    private static MergePlan DeleterAndHookerPlan(string highPriority, string lowPriority)
    {
        var path = StaleTalkFixture.TalkPath;
        var contents = new Dictionary<string, byte[]>
        {
            ["Deleter:" + path] = DeletedMachineTalkFixture.Deleter,
            ["Hooker:" + path] = DeletedMachineTalkFixture.Hooker,
        };
        return RealPlan(contents, new HashSet<string> { path }, _ => DeletedMachineTalkFixture.Vanilla, NoBase,
            Real(lowPriority, path, 1), Real(highPriority, path, 2));
    }

    [Fact]
    public void A_talk_pair_that_only_merges_with_the_other_mod_as_the_base_merges_that_way_and_says_so()
    {
        var plan = DeleterAndHookerPlan(highPriority: "Hooker", lowPriority: "Deleter");

        plan.Refusal.Should().BeNull();
        plan.Unmerged.Should().BeEmpty();
        var merged = plan.Merged.Should().ContainSingle().Which;
        var swapped = merged.Notes.OfType<MergeBaseChanged>().Should().ContainSingle().Which;
        swapped.Base.Should().Be("Deleter");
        swapped.Leader.Should().Be("Hooker");
        swapped.Error.Should().Contain("calls machine 20");
        swapped.Describe().Should().Contain("Deleter as the base").And.Contain("Hooker first").And.Contain("calls machine 20");
        merged.Contributors.Select(c => c.ModName).Should().Equal("Hooker", "Deleter");
    }

    [Fact]
    public void A_talk_pair_that_merges_with_the_priority_leader_as_the_base_is_left_alone()
    {
        var plan = DeleterAndHookerPlan(highPriority: "Deleter", lowPriority: "Hooker");

        plan.Unmerged.Should().BeEmpty();
        plan.Merged.Should().ContainSingle().Which.Notes.OfType<MergeBaseChanged>().Should().BeEmpty();
    }

    [Fact]
    public void The_file_a_swapped_base_gives_is_the_file_the_same_base_gives_when_it_leads_by_priority()
    {
        var swapped = DeleterAndHookerPlan(highPriority: "Hooker", lowPriority: "Deleter").Merged.Single().Bytes;
        var direct = DeleterAndHookerPlan(highPriority: "Deleter", lowPriority: "Hooker").Merged.Single().Bytes;

        swapped.Should().Equal(direct);
    }

    [Fact]
    public void A_talk_merge_that_succeeds_in_the_priority_order_is_tried_once_and_not_turned_round()
    {
        const string path = "script/talk/m00.talkesdbnd.dcx";
        var calls = new List<Call>();
        var index = MergePathIndex.Build([File("a", path, 2), File("b", path, 1)], Traits, new HashSet<string> { path });

        var plan = MergePlanner.Plan(index, ByPackage, Vanilla("van"), NoBase, Traits, _ => Recording(calls));

        calls.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Accumulated = "a", Next = "b" }, o => o.ExcludingMissingMembers());
        plan.Merged.Single().Notes.OfType<MergeBaseChanged>().Should().BeEmpty();
        plan.Merged.Single().Contributors.Select(c => c.PackageId).Should().Equal("a", "b");
    }

    [Fact]
    public void A_talk_pair_that_fails_in_every_order_falls_back_to_the_priority_winner_with_the_first_error()
    {
        // Archives holding different entries are refused before any machine is read, whichever is the base.
        const string path = "script/talk/m00.talkesdbnd.dcx";
        var contents = new Dictionary<string, byte[]>
        {
            ["high:" + path] = EsdMergeFixtureArchive(ER, [0, 1]),
            ["low:" + path] = EsdMergeFixtureArchive(ER, [0]),
        };

        var plan = RealPlan(contents, new HashSet<string> { path }, _ => EsdMergeFixtureArchive(ER, [0, 1]), NoBase, Real("low", path, 1), Real("high", path, 2));

        plan.Merged.Should().BeEmpty();
        var fallback = plan.Unmerged.Should().ContainSingle().Which;
        fallback.Winner!.Value.PackageId.Should().Be("high");
        fallback.VanillaSource.Should().NotBeEmpty("the report names the vanilla copy the merge was tried against");
    }

    [Fact]
    public void A_three_way_pair_of_message_archives_is_not_retried_with_another_base()
    {
        const string path = "msg/engus/item.msgbnd.dcx";
        var calls = 0;
        MergeFold fold = (_, _, _, _, _, _, _, _, _, _) => { calls++; throw new MergeException("cannot join"); };
        var index = Index(new HashSet<string> { path }, File("a", path, 2), File("b", path, 1));

        var plan = Plan(index, fold);

        plan.Unmerged.Should().ContainSingle();
        calls.Should().Be(1, "a three-way message merge refuses archives holding different tables whichever side leads");
    }

    [Fact]
    public void The_bases_tried_for_three_talk_mods_rotate_with_the_others_kept_in_priority_order()
    {
        const string path = "script/talk/m00.talkesdbnd.dcx";
        var seen = new List<string>();
        MergeFold fold = (acc, next, _, _, _, _, _, _, _, _) =>
        {
            seen.Add(S(acc) + ">" + S(next));
            if (S(acc) is "a" or "b") throw new MergeException("only c leads");
            return B("c");
        };
        var index = MergePathIndex.Build([File("a", path, 3), File("b", path, 2), File("c", path, 1)], Traits, new HashSet<string> { path });

        var plan = MergePlanner.Plan(index, ByPackage, Vanilla("van"), NoBase, Traits, s => s == MergeStrategy.EsdThreeWay ? fold : throw new InvalidOperationException());

        plan.Merged.Should().ContainSingle();
        seen.Should().Equal("a>b", "b>a", "c>a", "c>b");
        plan.Merged.Single().Notes.OfType<MergeBaseChanged>().Single().Base.Should().Be("c");
    }

    [Fact]
    public void A_talk_merge_that_fails_with_every_base_reports_the_priority_orders_error()
    {
        const string path = "script/talk/m00.talkesdbnd.dcx";
        MergeFold fold = (acc, _, _, _, _, _, _, _, _, _) => throw new MergeException($"{S(acc)} cannot lead");
        var index = MergePathIndex.Build([File("a", path, 3), File("b", path, 2), File("c", path, 1)], Traits, new HashSet<string> { path });

        var plan = MergePlanner.Plan(index, ByPackage, Vanilla("van"), NoBase, Traits, _ => fold);

        plan.Merged.Should().BeEmpty();
        var fallback = plan.Unmerged.Should().ContainSingle().Which;
        fallback.Outcome.Reason.Should().Contain("a cannot lead").And.NotContain("b cannot lead").And.NotContain("c cannot lead");
        fallback.Winner!.Value.PackageId.Should().Be("a");
    }

    [Fact]
    public void A_message_union_whose_leader_lacks_a_table_the_other_has_merges_with_the_other_as_the_base()
    {
        // The union keeps the base's table list, so it refuses a table only the other side has; leading with the side
        // that holds both keeps every table.
        const string path = "msg/engus/item.msgbnd.dcx";
        var contents = new Dictionary<string, byte[]>
        {
            ["high:" + path] = Msgbnd((1, [(100, "high's text")])),
            ["low:" + path] = Msgbnd((1, [(100, "low's text"), (200, "only low")]), (2, [(300, "low's second table")])),
        };

        var plan = RealPlan(contents, NoVanilla, _ => null, NoBase, Real("low", path, 1), Real("high", path, 2));

        plan.Unmerged.Should().BeEmpty();
        var merged = plan.Merged.Should().ContainSingle().Which;
        merged.Strategy.Should().Be(MergeStrategy.FmgUnion);
        merged.Contributors.Select(c => c.PackageId).Should().Equal("high", "low");
        var swapped = merged.Notes.OfType<MergeBaseChanged>().Should().ContainSingle().Which;
        swapped.Base.Should().Be("low");
        swapped.Leader.Should().Be("high");
        swapped.Error.Should().Contain("entries [2]");
        var tables = MsgbndFixture.ReadBack(merged.Bytes, ER);
        tables.Keys.Should().BeEquivalentTo(new[] { 1, 2 });
        tables[1].Should().Equal(new Dictionary<int, string?> { [100] = "low's text", [200] = "only low" });
        tables[2].Should().Equal(new Dictionary<int, string?> { [300] = "low's second table" });
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
