using FluentAssertions;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Merge;
using NexusMods.Hashing.xxHash3;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

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

    private static ProviderFile File(string package, string rel, ulong priority = 1, string? name = null, bool hasPriority = true)
        => new(package, rel, Hash.From((ulong)(package.GetHashCode() ^ rel.GetHashCode())), priority, name ?? package, HasPriority: hasPriority);

    private static string? WinnerOf(params ProviderFile[] files)
        => Build(null, files).Single().Winner?.PackageId;

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
    public void Case_variant_spellings_from_different_packages_merge_as_one_target()
    {
        var candidate = Build(null, File("a", "Menu/X.tpf.dcx", 1), File("b", "menu/x.tpf.dcx", 5)).Single();

        candidate.Spellings.Should().BeEquivalentTo(["Menu/X.tpf.dcx", "menu/x.tpf.dcx"]);
        candidate.Path.Should().Be("menu/x.tpf.dcx", "the highest-priority contributor's spelling names the merged file");
        candidate.Strategy.Should().Be(MergeStrategy.TpfUnion);
        candidate.Outcome.Should().Be(MergeOutcome.WillMerge);
    }

    [Fact]
    public void Case_variant_spellings_with_no_strategy_are_a_case_only_collision()
    {
        var candidate = Build(null, File("a", "parts/Wp.dcx"), File("b", "parts/wp.dcx")).Single();

        candidate.Spellings.Should().BeEquivalentTo(["parts/Wp.dcx", "parts/wp.dcx"]);
        candidate.Outcome.Should().Be(MergeOutcome.NotMergeable(MergeOutcome.CaseOnlyCollision));
    }

    [Fact]
    public void Four_providers_of_a_message_archive_under_two_spellings_merge_under_the_top_priority_spelling()
    {
        var candidate = Build(null,
            File("NoFallDead", "msg/engUS/item_dlc02.msgbnd.dcx", 4),
            File("GreatRuneOverhaul", "msg/engus/item_dlc02.msgbnd.dcx", 9),
            File("movesets", "msg/engUS/item_dlc02.msgbnd.dcx", 2),
            File("BossResurrection", "msg/engus/item_dlc02.msgbnd.dcx", 1)).Single();

        candidate.Strategy.Should().Be(MergeStrategy.FmgUnion);
        candidate.Outcome.Should().Be(MergeOutcome.WillMerge);
        candidate.Path.Should().Be("msg/engus/item_dlc02.msgbnd.dcx");
        candidate.Contributors.Select(c => c.PackageId).Should().Equal("GreatRuneOverhaul", "NoFallDead", "movesets", "BossResurrection");
    }

    [Fact]
    public void A_priority_tie_between_spellings_goes_to_the_first_package_id()
    {
        var candidate = Build(null, File("b", "msg/engus/Item.msgbnd.dcx", 3), File("a", "msg/engus/item.msgbnd.dcx", 3)).Single();

        candidate.Path.Should().Be("msg/engus/item.msgbnd.dcx");
        candidate.Outcome.Should().Be(MergeOutcome.WillMerge);
    }

    [Fact]
    public void A_package_holding_two_spellings_stays_refused_even_when_other_packages_provide_the_path()
    {
        var candidate = Build(null,
            File("a", "msg/engus/Item.msgbnd.dcx"), File("a", "msg/engus/item.msgbnd.dcx"), File("b", "msg/engus/item.msgbnd.dcx")).Single();

        candidate.Outcome.Should().Be(MergeOutcome.NotMergeable(MergeOutcome.CaseOnlyCollision));
    }

    [Fact]
    public void Case_variants_of_a_path_whose_merge_needs_a_vanilla_copy_wait_for_one()
    {
        var candidate = Build(null, File("a", "script/talk/M00.talkesdbnd.dcx"), File("b", "script/talk/m00.talkesdbnd.dcx")).Single();

        candidate.Outcome.Should().Be(MergeOutcome.NotMergeable(MergeOutcome.NoVanillaSource));
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
    public void The_winner_of_a_path_without_a_strategy_is_the_highest_priority_package()
    {
        WinnerOf(File("a", "map/m10.mapbnd.dcx", 5), File("b", "map/m10.mapbnd.dcx", 1)).Should().Be("a");
        WinnerOf(File("a", "map/m10.mapbnd.dcx", 1), File("b", "map/m10.mapbnd.dcx", 5)).Should().Be("b");
    }

    [Fact]
    public void A_package_with_no_priority_wins_over_one_with_a_priority()
    {
        // me3 lists a package it has no priority for after the others, and the last package listed wins.
        WinnerOf(
            File("listed", "map/m10.mapbnd.dcx", 9),
            File("unlisted", "map/m10.mapbnd.dcx", 0, hasPriority: false)).Should().Be("unlisted");
    }

    [Fact]
    public void A_package_ranks_by_the_highest_priority_of_any_mod_in_its_folder()
    {
        // Package a also holds a file from a mod at 9, so it outranks b's 5 even though a's copy of this path is at 1.
        WinnerOf(
            File("a", "map/m10.mapbnd.dcx", 1),
            File("a", "other/readme.txt", 9),
            File("b", "map/m10.mapbnd.dcx", 5)).Should().Be("a");
    }

    [Fact]
    public void A_winning_package_two_mods_share_is_named_after_the_higher_mod()
    {
        var winner = Build(null,
            File("a", "map/m10.mapbnd.dcx", 1, "Low"),
            File("a", "map/m10.mapbnd.dcx", 9, "High"),
            File("b", "map/m10.mapbnd.dcx", 5)).Single().Winner;

        winner?.ModName.Should().Be("High");
    }

    [Fact]
    public void Equal_priorities_make_the_last_package_id_the_winner()
    {
        WinnerOf(File("a", "map/m10.mapbnd.dcx", 3), File("b", "map/m10.mapbnd.dcx", 3)).Should().Be("b");
    }

    [Fact]
    public void A_merged_or_case_only_path_names_no_winner()
    {
        WinnerOf(File("a", "msg/engus/item.msgbnd.dcx"), File("b", "msg/engus/item.msgbnd.dcx")).Should().BeNull();
        WinnerOf(File("a", "map/M10.mapbnd.dcx"), File("b", "map/m10.mapbnd.dcx")).Should().BeNull();
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
