using FluentAssertions;
using NexusMods.Games.FromSoft.Formats;
using NexusMods.Games.FromSoft.Merge;
using NexusMods.Hashing.xxHash3;

namespace NexusMods.Games.FromSoft.Tests.Merge;

/// <summary>
/// Ports of the erm conflict-planning tests that still apply once nothing is declared:
/// test_index_lists_every_mod_claiming_a_path, test_index_ignores_mods_not_in_the_profile,
/// test_index_tolerates_a_listed_mod_that_was_never_installed, test_no_collision_is_a_no_op,
/// test_merge_is_skipped_when_only_one_side_is_installed, test_one_provider_without_a_base_is_still_skipped,
/// test_case_only_path_collision_is_detected, test_no_supplied_base_keeps_prefer_leading,
/// test_resolve_folds_a_merge_across_three_providers.
/// </summary>
public class MergePathIndexTests
{
    private static readonly FromSoftFormatTraits Traits = MergeStrategiesTests.WithRegulation("regulation.test");
    private static readonly IReadOnlySet<string> NoVanilla = new HashSet<string>();

    private static ProviderFile File(string package, string rel, ulong priority = 1, string? name = null)
        => new(package, rel, Hash.From((ulong)(package.GetHashCode() ^ rel.GetHashCode())), priority, name ?? package);

    private static IReadOnlyList<MergeCandidate> Build(IReadOnlySet<string>? vanilla, params ProviderFile[] files)
        => MergePathIndex.Build(files, Traits, vanilla ?? NoVanilla).Candidates;

    [Fact]
    public void Index_lists_every_mod_claiming_a_path()
    {
        var candidates = Build(null, File("a", "regulation.test"), File("b", "regulation.test"), File("c", "regulation.test"));

        var candidate = candidates.Should().ContainSingle().Subject;
        candidate.Contributors.Select(c => c.PackageId).Should().BeEquivalentTo(["a", "b", "c"]);
    }

    [Fact]
    public void A_disabled_mod_is_absent_from_input_and_output()
    {
        // Disabled mods never reach the index; the two that remain are all it can list.
        var candidates = Build(null, File("a", "regulation.test"), File("c", "regulation.test"));

        candidates.Single().Contributors.Select(c => c.PackageId).Should().BeEquivalentTo(["a", "c"]);
    }

    [Fact]
    public void A_listed_mod_that_provides_nothing_is_tolerated()
    {
        var candidates = Build(null, File("a", "regulation.test"), File("b", "other.txt"));

        candidates.Should().BeEmpty();
    }

    [Fact]
    public void No_collision_is_a_no_op()
    {
        Build(null, File("a", "one.tpf.dcx"), File("b", "two.tpf.dcx")).Should().BeEmpty();
    }

    [Fact]
    public void One_side_installed_is_skipped()
    {
        Build(null, File("a", "menu/x.tpf.dcx")).Should().BeEmpty();
    }

    [Fact]
    public void One_provider_without_a_base_is_still_skipped()
    {
        Build(NoVanilla, File("a", "msg/engus/item.msgbnd.dcx")).Should().BeEmpty();
    }

    [Fact]
    public void Case_only_path_collision_between_packages_is_not_mergeable()
    {
        var candidate = Build(null, File("a", "Menu/X.tpf.dcx"), File("b", "menu/x.tpf.dcx")).Single();

        candidate.Spellings.Should().BeEquivalentTo(["Menu/X.tpf.dcx", "menu/x.tpf.dcx"]);
        candidate.Outcome.Should().Be(MergeOutcome.NotMergeable(MergeOutcome.CaseOnlyCollision));
    }

    [Fact]
    public void One_package_under_two_spellings_is_a_case_only_collision()
    {
        var candidate = Build(null, File("a", "Menu/X.tpf.dcx"), File("a", "menu/x.tpf.dcx")).Single();

        candidate.Outcome.Should().Be(MergeOutcome.NotMergeable(MergeOutcome.CaseOnlyCollision));
    }

    [Fact]
    public void One_package_under_one_spelling_is_not_listed()
    {
        Build(null, File("a", "menu/x.tpf.dcx")).Should().BeEmpty();
    }

    [Fact]
    public void The_highest_priority_leads()
    {
        var candidate = Build(null, File("low", "regulation.test", 1), File("high", "regulation.test", 9)).Single();

        candidate.Contributors.Select(c => c.PackageId).Should().Equal("high", "low");
    }

    [Fact]
    public void Three_contributors_come_out_in_priority_order()
    {
        var candidate = Build(null,
            File("m", "regulation.test", 5),
            File("z", "regulation.test", 9),
            File("a", "regulation.test", 1)).Single();

        candidate.Contributors.Select(c => c.PackageId).Should().Equal("z", "m", "a");
        candidate.Outcome.Should().Be(MergeOutcome.WillMerge);
    }

    [Fact]
    public void Equal_priorities_fall_back_to_the_package_id()
    {
        var candidate = Build(null, File("b", "regulation.test", 4), File("a", "regulation.test", 4)).Single();

        candidate.Contributors.Select(c => c.PackageId).Should().Equal("a", "b");
    }

    [Fact]
    public void The_regulation_merges_as_param_rows_and_a_nested_copy_does_not()
    {
        var plain = Build(null, File("a", "regulation.test"), File("b", "regulation.test")).Single();
        plain.Strategy.Should().Be(MergeStrategy.ParamRows);
        plain.Outcome.Should().Be(MergeOutcome.WillMerge);

        var nested = Build(null, File("a", "sub/regulation.test"), File("b", "sub/regulation.test")).Single();
        nested.Strategy.Should().BeNull();
        nested.Outcome.Should().Be(MergeOutcome.NotMergeable(MergeOutcome.NoStrategy));
    }

    [Fact]
    public void A_regulation_spelled_from_another_games_traits_merges_too()
    {
        var traits = MergeStrategiesTests.WithRegulation("other.dat");
        var index = MergePathIndex.Build([File("a", "other.dat"), File("b", "other.dat")], traits, NoVanilla);

        index.Candidates.Single().Strategy.Should().Be(MergeStrategy.ParamRows);
    }

    [Fact]
    public void A_msgbnd_overlap_is_a_union_without_vanilla_and_three_way_with_it()
    {
        const string path = "msg/engus/item.msgbnd.dcx";
        var files = new[] { File("a", path), File("b", path) };

        var union = MergePathIndex.Build(files, Traits, NoVanilla).Candidates.Single();
        union.Strategy.Should().Be(MergeStrategy.FmgUnion);
        union.Outcome.Should().Be(MergeOutcome.WillMerge);

        var threeWay = MergePathIndex.Build(files, Traits, new HashSet<string> { path }).Candidates.Single();
        threeWay.Strategy.Should().Be(MergeStrategy.FmgThreeWay);
        threeWay.Outcome.Should().Be(MergeOutcome.WillMerge);
    }

    [Fact]
    public void A_tpf_overlap_is_a_union()
    {
        var candidate = Build(null, File("a", "menu/x.tpf.dcx"), File("b", "menu/x.tpf.dcx")).Single();

        candidate.Strategy.Should().Be(MergeStrategy.TpfUnion);
        candidate.Outcome.Should().Be(MergeOutcome.WillMerge);
    }

    [Fact]
    public void An_esd_overlap_needs_a_vanilla_source()
    {
        const string path = "script/talk/m00.talkesdbnd.dcx";
        var files = new[] { File("a", path), File("b", path) };

        var without = MergePathIndex.Build(files, Traits, NoVanilla).Candidates.Single();
        without.Strategy.Should().Be(MergeStrategy.EsdThreeWay);
        without.Outcome.Should().Be(MergeOutcome.NotMergeable(MergeOutcome.NoVanillaSource));

        var with = MergePathIndex.Build(files, Traits, new HashSet<string> { path }).Candidates.Single();
        with.Strategy.Should().Be(MergeStrategy.EsdThreeWay);
        with.Outcome.Should().Be(MergeOutcome.WillMerge);
    }

    [Fact]
    public void A_dcx_without_a_strategy_is_not_mergeable()
    {
        var candidate = Build(null, File("a", "map/m10.mapbnd.dcx"), File("b", "map/m10.mapbnd.dcx")).Single();

        candidate.Strategy.Should().BeNull();
        candidate.Outcome.Should().Be(MergeOutcome.NotMergeable(MergeOutcome.NoStrategy));
    }

    [Fact]
    public void Files_of_the_previous_merge_never_count_as_providers()
    {
        var candidates = Build(null,
            File(MergePathIndex.MergedPackageId, "menu/x.tpf.dcx", 99),
            File("a", "menu/x.tpf.dcx"));

        candidates.Should().BeEmpty();
    }

    [Fact]
    public void Candidates_are_sorted_by_path_and_found_by_lookup()
    {
        var index = MergePathIndex.Build([
            File("a", "menu/z.tpf.dcx"), File("b", "menu/z.tpf.dcx"),
            File("a", "menu/b.tpf.dcx"), File("b", "menu/b.tpf.dcx"),
        ], Traits, NoVanilla);

        index.Candidates.Select(c => c.Path).Should().Equal("menu/b.tpf.dcx", "menu/z.tpf.dcx");
        index.Lookup("MENU/Z.tpf.dcx")!.Path.Should().Be("menu/z.tpf.dcx");
        index.Lookup("menu/none.tpf.dcx").Should().BeNull();
    }
}
