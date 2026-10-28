using System.Text;
using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Games.Merge.Tests.Fixtures;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

namespace SilverModManager.Abstractions.Games.Merge.Tests;

public class MergeContributorsTests(IServiceProvider provider) : AGameTest<StubbedGame>(provider)
{
    private readonly AMergeGame _game = new TestMergeGame();

    /// <summary>A mod group with the given files, each holding its own path as text, and (unless priority is null) a priority row.</summary>
    private async Task<LoadoutItemGroupId> Mod(Loadout.ReadOnly loadout, string name, ulong? priority, params string[] files)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, name);
        foreach (var file in files) AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, file));
        if (priority is { } p)
        {
            _ = new LoadoutItemGroupPriority.New(tx)
            {
                LoadoutId = loadout.LoadoutId,
                TargetId = group,
                Priority = ConflictPriority.From(p),
            };
        }
        var result = await tx.Commit();
        return result[group];
    }

    private IReadOnlyList<ContestedPath> Contested(Loadout.ReadOnly loadout) => MergeContributors.Of(Refresh(loadout), _game);

    [Fact]
    public async Task FilesUnderTwoModFoldersWithTheSameRelativePathAreOneContest()
    {
        var loadout = await CreateLoadout();
        await Mod(loadout, "A", 2, "Mods/modA/scripts/x.txt");
        await Mod(loadout, "B", 1, "Mods/modB/scripts/x.txt");

        var contest = Contested(loadout).Should().ContainSingle().Subject;

        contest.Rel.Should().Be("scripts/x.txt");
        contest.Contributors.Select(c => c.Package).Should().Equal("modA", "modB");
        contest.Contributors.Select(c => c.ModName).Should().Equal("A", "B");
        contest.Contributors.Select(c => c.Rank).Should().Equal(2UL, 1UL);
        contest.Strategy.Should().BeSameAs(TextThreeWayMerge.Instance);
    }

    [Fact]
    public async Task APackageTheGameSaysDoesNotContributeIsLeftOutOfTheContest()
    {
        var loadout = await CreateLoadout();
        await Mod(loadout, "A", 3, "Mods/modA/scripts/x.txt");
        await Mod(loadout, "B", 2, "Mods/modB/scripts/x.txt");
        await Mod(loadout, "C", 1, "Mods/modC/scripts/x.txt");

        var contest = MergeContributors.Of(Refresh(loadout), new LeavingOut("modB")).Should().ContainSingle().Subject;
        contest.Contributors.Select(c => c.Package).Should().Equal("modA", "modC");

        MergeContributors.Of(Refresh(loadout), _game).Single().Contributors.Should().HaveCount(3, "a game that does not say otherwise counts every package");
    }

    private sealed class LeavingOut(string package) : TestMergeGame
    {
        public override bool Contributes(Loadout.ReadOnly loadout, string candidate) => candidate != package;
    }

    [Fact]
    public async Task RankIsAskedOncePerModFolderNotOncePerFile()
    {
        var loadout = await CreateLoadout();
        await Mod(loadout, "A", 2, "Mods/modA/scripts/x.txt", "Mods/modA/scripts/y.txt", "Mods/modA/scripts/z.txt");
        await Mod(loadout, "B", 1, "Mods/modB/scripts/x.txt", "Mods/modB/scripts/y.txt");
        var game = new CountingMergeGame();

        MergeContributors.Of(Refresh(loadout), game).Should().HaveCount(2);

        game.RankCalls.Should().Be(2);
    }

    [Fact]
    public async Task ModsThatShareANameAreToldApartByTheirFolderAndOthersKeepTheirName()
    {
        var loadout = await CreateLoadout();
        await Mod(loadout, "Foo", 3, "Mods/modA/scripts/x.txt");
        await Mod(loadout, "Foo", 2, "Mods/modB/scripts/x.txt");
        await Mod(loadout, "Bar", 1, "Mods/modC/scripts/x.txt");

        var contest = Contested(loadout).Should().ContainSingle().Subject;

        contest.Contributors.Select(c => c.ModName).Should().Equal("Foo (modA)", "Foo (modB)", "Bar");
    }

    [Fact]
    public async Task CaseOnlySpellingsGroupTogetherAndTheWinnerSpellingIsKept()
    {
        var loadout = await CreateLoadout();
        await Mod(loadout, "A", 2, "Mods/modA/Scripts/X.txt");
        await Mod(loadout, "B", 1, "Mods/modB/scripts/x.txt");

        var contest = Contested(loadout).Should().ContainSingle().Subject;

        contest.Rel.Should().Be("Scripts/X.txt");
        contest.Contributors.Should().HaveCount(2);
    }

    [Fact]
    public async Task AFileOnlyOneModProvidesIsNotContested()
    {
        var loadout = await CreateLoadout();
        await Mod(loadout, "A", 2, "Mods/modA/scripts/only-a.txt", "Mods/modA/scripts/x.txt");
        await Mod(loadout, "B", 1, "Mods/modB/scripts/x.txt");

        Contested(loadout).Select(c => c.Rel).Should().Equal("scripts/x.txt");
    }

    [Fact]
    public async Task ADisabledModDoesNotContribute()
    {
        var loadout = await CreateLoadout();
        await Mod(loadout, "A", 2, "Mods/modA/scripts/x.txt");
        var b = await Mod(loadout, "B", 1, "Mods/modB/scripts/x.txt");
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(LoadoutItem.Load(Connection.Db, b).Id, LoadoutItem.Disabled, Null.Instance);
            await tx.Commit();
        }

        Contested(loadout).Should().BeEmpty();
    }

    [Fact]
    public async Task ModsWithoutPriorityRowsRankBelowEveryoneAndOrderByPackageName()
    {
        var loadout = await CreateLoadout();
        await Mod(loadout, "B", null, "Mods/modB/scripts/x.txt");
        await Mod(loadout, "A", null, "Mods/modA/scripts/x.txt");

        var contest = Contested(loadout).Should().ContainSingle().Subject;

        contest.Contributors.Select(c => c.Rank).Should().OnlyContain(r => r == ConflictPriority.MinValue.Value);
        contest.Contributors.Select(c => c.Package).Should().Equal("modA", "modB");
    }

    [Fact]
    public async Task AContestedFileTheGameDoesNotMergeIsReturnedWithoutAStrategy()
    {
        var loadout = await CreateLoadout();
        await Mod(loadout, "A", 2, "Mods/modA/x.bin");
        await Mod(loadout, "B", 1, "Mods/modB/x.bin");

        var contest = Contested(loadout).Should().ContainSingle().Subject;

        contest.Rel.Should().Be("x.bin");
        contest.Strategy.Should().BeNull();
    }

    [Fact]
    public async Task AFileOutsideEveryModFolderIsIgnored()
    {
        var loadout = await CreateLoadout();
        await Mod(loadout, "A", 2, "Mods/readme.txt");
        await Mod(loadout, "B", 1, "Mods/readme.txt");

        Contested(loadout).Should().BeEmpty();
    }

    [Fact]
    public async Task APathTheGameSaysIsNotAContestIsIgnoredButCanBeAskedFor()
    {
        var loadout = await CreateLoadout();
        await Mod(loadout, "A", 2, "Mods/modA/content/blob0.bundle");
        await Mod(loadout, "B", 1, "Mods/modB/content/blob0.bundle");
        loadout = Refresh(loadout);

        MergeContributors.Of(loadout, _game).Should().BeEmpty();

        var ignored = MergeContributors.NotContested(loadout, _game).Should().ContainSingle().Subject;
        ignored.Rel.Should().Be("content/blob0.bundle");
        ignored.Strategy.Should().BeNull();
        ignored.Contributors.Select(c => c.ModName).Should().Equal("A", "B");
    }

    [Fact]
    public async Task OneModShippingThePathInTwoPackageFoldersDoesNotContestItself()
    {
        var loadout = await CreateLoadout();
        await Mod(loadout, "A", 2, "Mods/modA1/scripts/x.txt", "Mods/modA2/scripts/x.txt");

        Contested(loadout).Should().BeEmpty();
    }

    [Fact]
    public async Task OneModInTwoPackageFoldersCountsOnceAgainstAnotherMod()
    {
        var loadout = await CreateLoadout();
        await Mod(loadout, "A", 2, "Mods/modA1/scripts/x.txt", "Mods/modA2/scripts/x.txt");
        await Mod(loadout, "B", 1, "Mods/modB/scripts/x.txt");

        var contest = Contested(loadout).Should().ContainSingle().Subject;

        contest.Contributors.Select(c => c.ModName).Should().Equal("A", "B");
        contest.Contributors[0].Package.Should().Be("modA1");
    }

    [Fact]
    public async Task TwoModsShippingTheSameExactTargetCollapseToTheHigherRankedOne()
    {
        var loadout = await CreateLoadout();
        await Mod(loadout, "A", 3, "Mods/shared/scripts/x.txt");
        await Mod(loadout, "B", 2, "Mods/shared/scripts/x.txt");
        await Mod(loadout, "C", 1, "Mods/modC/scripts/x.txt");

        var contest = Contested(loadout).Should().ContainSingle().Subject;

        contest.Contributors.Select(c => c.ModName).Should().Equal("A", "C");
    }

    [Fact]
    public async Task TwoModsShippingOnlyTheSameExactTargetAreNotContestedHere()
    {
        var loadout = await CreateLoadout();
        await Mod(loadout, "A", 2, "Mods/shared/scripts/x.txt");
        await Mod(loadout, "B", 1, "Mods/shared/scripts/x.txt");

        Contested(loadout).Should().BeEmpty();
    }

    [Fact]
    public async Task APinOnTheLowerPriorityModDecidesWhichCopyOfTheSharedTargetContributes()
    {
        var loadout = await CreateLoadout();
        var a = await Mod(loadout, "A", 3, "Mods/shared/scripts/x.txt");
        var b = await Mod(loadout, "B", 2, "Mods/shared/scripts/x.txt");
        await Mod(loadout, "C", 1, "Mods/modC/scripts/x.txt");
        await LoadoutManager.PinFileWinner(loadout.LoadoutId, new GamePath(LocationId.Game, "Mods/shared/scripts/x.txt"), b);

        var contest = Contested(loadout).Should().ContainSingle().Subject;

        a.Should().NotBe(b);
        contest.Contributors.Select(c => c.ModName).Should().Equal("B", "C");
    }

    [Fact]
    public async Task AFileInAGroupWithNoPriorityRowRanksZeroEvenInsideAModThatHasOne()
    {
        var loadout = await CreateLoadout();
        var m = await Mod(loadout, "M", 3);
        LoadoutItemGroupId child;
        using (var tx = Connection.BeginTransaction())
        {
            var group = new LoadoutItemGroup.New(tx, out var id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id) { LoadoutId = loadout.LoadoutId, Name = "M inner", ParentId = m },
            };
            AddFile(tx, loadout.LoadoutId, group.Id, new GamePath(LocationId.Game, "Mods/shared/scripts/x.txt"));
            child = (await tx.Commit())[group.Id];
        }
        await Mod(loadout, "N", 2, "Mods/shared/scripts/x.txt");
        await Mod(loadout, "C", 1, "Mods/modC/scripts/x.txt");

        var contest = Contested(loadout).Should().ContainSingle().Subject;

        child.Should().NotBe(m);
        contest.Contributors.Select(c => c.ModName).Should().Equal("N", "C");
    }
}
