using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Games.Merge.Tests.Fixtures;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

namespace SilverModManager.Abstractions.Games.Merge.Tests;

public class MergeEngineTests(IServiceProvider provider) : AGameTest<StubbedGame>(provider)
{
    private const string Vanilla = "a\nb\nc\n";
    private const string X = "scripts/x.txt";

    /// <summary>One trust answer per test, so parallel test classes cannot flip each other's.</summary>
    private sealed class Trust : IVanillaTrust
    {
        public bool Trusted = true;
        public string Reason = "listed as modified";

        public bool IsTrusted(GameInstallation installation, GamePath path, Hash currentHash, out string reason)
        {
            reason = Trusted ? "" : Reason;
            return Trusted;
        }
    }

    private readonly Trust _trust = new();
    private readonly AMergeGame _game = new TestMergeGame();

    private MergeEngine Engine() => new(FileStore, _trust, NullLogger<MergeEngine>.Instance, _ => _game);

    private async Task<Hash> AddModFile(ITransaction tx, LoadoutId loadoutId, LoadoutItemGroupId groupId, GamePath path, byte[] bytes)
    {
        AddFile(tx, loadoutId, groupId, path, bytes, out var hash, out var size);
        await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(path.Path, new MemoryStream(bytes)), hash, size)]);
        return hash;
    }

    /// <summary>A mod group named <paramref name="name"/> with priority <paramref name="priority"/> and the given files under Mods/&lt;package&gt;/.</summary>
    private async Task<LoadoutItemGroupId> Mod(Loadout.ReadOnly loadout, string name, string package, ulong priority, params (string Rel, byte[] Bytes)[] files)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, name);
        foreach (var (rel, bytes) in files)
            await AddModFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, $"Mods/{package}/{rel}"), bytes);
        _ = new LoadoutItemGroupPriority.New(tx)
        {
            LoadoutId = loadout.LoadoutId,
            TargetId = group,
            Priority = ConflictPriority.From(priority),
        };
        var result = await tx.Commit();
        return result[group];
    }

    private static (string Rel, byte[] Bytes) Text(string rel, string text) => (rel, Encoding.UTF8.GetBytes(text));

    private async Task WriteVanilla(string rel, string text)
    {
        var path = GameInstallation.Locations[LocationId.Game].Path.Combine($"vanilla/{rel}");
        path.Parent.CreateDirectory();
        await path.WriteAllTextAsync(text);
    }

    private async Task SetPriority(LoadoutItemGroupId group, ulong priority)
    {
        using var tx = Connection.BeginTransaction();
        foreach (var row in LoadoutItemGroupPriority.FindByTarget(Connection.Db, group))
            tx.Add(row.Id, LoadoutItemGroupPriority.Priority, ConflictPriority.From(priority));
        await tx.Commit();
    }

    /// <summary>The standard contest: vanilla "a b c", A (2) and B (1) both rewrite line 2.</summary>
    private async Task<(Loadout.ReadOnly Loadout, LoadoutItemGroupId A, LoadoutItemGroupId B)> OverlappingEdits()
    {
        var loadout = await CreateLoadout();
        await WriteVanilla(X, Vanilla);
        var a = await Mod(loadout, "A", "modA", 2, Text(X, "a\nB1\nc\n"));
        var b = await Mod(loadout, "B", "modB", 1, Text(X, "a\nB2\nc\n"));
        return (Refresh(loadout), a, b);
    }

    private static string AsText(MergedOutput output) => Encoding.UTF8.GetString(output.Bytes);

    [Fact]
    public async Task OverlappingEditsAreMergedWithTheHigherPriorityModWinningAndALossNoted()
    {
        var (loadout, _, _) = await OverlappingEdits();
        var engine = Engine();

        var output = engine.Plan(loadout, MergeSourceMode.Apply).Should().ContainSingle().Subject;

        output.Output.Should().Be(new GamePath(LocationId.Game, "Mods/merged/scripts/x.txt"));
        output.RelPath.Should().Be(X);
        AsText(output).Should().Be("a\nB1\nc\n");
        output.Contributors.Should().Equal("A", "B");
        output.Strategy.Should().Be("text-3way");
        output.Notes.OfType<LinesKept>().Should().ContainSingle().Which.Should().Match<LinesKept>(n => n.KeptMod == "A" && n.DroppedMod == "B");

        var entry = engine.Report(loadout, MergeSourceMode.Report).Entries.Should().ContainSingle().Subject;
        entry.Path.Should().Be(X);
        entry.Outcome.Should().Be(MergeReportOutcome.Merged);
        entry.Strategy.Should().Be("text-3way");
        entry.Contributors.Should().Equal("A", "B");
        entry.VanillaSource.Should().Be("the game's own copy");
        entry.Reason.Should().BeEmpty();
        entry.Notes.Should().ContainSingle().Which.Should().Contain("kept A's");
    }

    [Fact]
    public async Task AReorderReplansBecauseRankIsInTheCacheKey()
    {
        var (loadout, a, b) = await OverlappingEdits();
        var engine = Engine();
        AsText(engine.Plan(loadout, MergeSourceMode.Apply).Single()).Should().Be("a\nB1\nc\n");

        await SetPriority(a, 1);
        await SetPriority(b, 2);
        var output = engine.Plan(Refresh(loadout), MergeSourceMode.Apply).Single();

        AsText(output).Should().Be("a\nB2\nc\n");
        output.Contributors.Should().Equal("B", "A");
        output.Notes.OfType<LinesKept>().Should().ContainSingle().Which.Should().Match<LinesKept>(n => n.KeptMod == "B" && n.DroppedMod == "A");
    }

    [Fact]
    public async Task AnUnchangedLoadoutReturnsTheCachedPlanInstance()
    {
        var (loadout, _, _) = await OverlappingEdits();
        var engine = Engine();

        var first = engine.Plan(loadout, MergeSourceMode.Apply);
        var second = engine.Plan(Refresh(loadout), MergeSourceMode.Apply);

        second.Should().BeSameAs(first);
    }

    [Fact]
    public async Task AnUntrustedVanillaMakesTheMergeTwoWayWithANote()
    {
        var (loadout, _, _) = await OverlappingEdits();
        _trust.Trusted = false;
        var engine = Engine();

        AsText(engine.Plan(loadout, MergeSourceMode.Apply).Single()).Should().Be("a\nB1\nc\n");
        engine.Report(loadout, MergeSourceMode.Report).Entries.Single().VanillaSource
            .Should().Contain("was not used as the base (listed as modified)");
    }

    [Fact]
    public async Task AMissingVanillaMakesTheMergeTwoWayWithANote()
    {
        var loadout = await CreateLoadout();
        // Tests share one game folder, so the file this one lacks a vanilla copy of has a name no other test writes.
        const string rel = "scripts/shipped-by-mods-only.txt";
        await Mod(loadout, "A", "modA", 2, Text(rel, "a\nB1\nc\n"));
        await Mod(loadout, "B", "modB", 1, Text(rel, "a\nB2\nc\n"));
        loadout = Refresh(loadout);
        var engine = Engine();

        AsText(engine.Plan(loadout, MergeSourceMode.Apply).Single()).Should().Be("a\nB1\nc\n");
        engine.Report(loadout, MergeSourceMode.Report).Entries.Single().VanillaSource.Should().Contain("no vanilla copy");
    }

    [Fact]
    public async Task ABaselineRebuildReplansBecauseTrustIsInTheCacheKey()
    {
        var loadout = await CreateLoadout();
        await WriteVanilla(X, Vanilla);
        await Mod(loadout, "A", "modA", 2, Text(X, "A\nb\nc\n"));
        await Mod(loadout, "B", "modB", 1, Text(X, "a\nb\nC\n"));
        loadout = Refresh(loadout);
        var engine = Engine();

        AsText(engine.Plan(loadout, MergeSourceMode.Apply).Single()).Should().Be("A\nb\nC\n");
        _trust.Trusted = false;

        AsText(engine.Plan(loadout, MergeSourceMode.Apply).Single()).Should().Be("A\nb\nc\n");
    }

    /// <summary>A contest over its own rel whose result depends on the vanilla copy: A edits line 1, B line 3.</summary>
    private async Task<(Loadout.ReadOnly Loadout, string VanillaPath)> TwoEditsOverOwnVanilla()
    {
        var loadout = await CreateLoadout();
        var rel = $"scripts/fingerprint-{Guid.NewGuid():N}.txt";
        await WriteVanilla(rel, Vanilla);
        await Mod(loadout, "A", "modA", 2, Text(rel, "A\nb\nc\n"));
        await Mod(loadout, "B", "modB", 1, Text(rel, "a\nb\nC\n"));
        return (Refresh(loadout), GameInstallation.Locations[LocationId.Game].Path.Combine($"vanilla/{rel}").ToString());
    }

    [Fact]
    public async Task AVanillaRewrittenWithItsSizeAndTimeKeptIsNotReadAgain()
    {
        var (loadout, vanilla) = await TwoEditsOverOwnVanilla();
        var engine = Engine();
        var first = engine.Plan(loadout, MergeSourceMode.Apply);
        AsText(first.Single()).Should().Be("A\nb\nC\n");

        // Same length, same time: nothing the fingerprint looks at has moved, so the cached hash and plan stand.
        var time = File.GetLastWriteTimeUtc(vanilla);
        await File.WriteAllTextAsync(vanilla, "a\nb\nC\n");
        File.SetLastWriteTimeUtc(vanilla, time);

        engine.Plan(loadout, MergeSourceMode.Apply).Should().BeSameAs(first);
    }

    [Fact]
    public async Task AVanillaWithANewModifiedTimeIsReadAgainAndReplanned()
    {
        var (loadout, vanilla) = await TwoEditsOverOwnVanilla();
        var engine = Engine();
        var first = engine.Plan(loadout, MergeSourceMode.Apply);

        var time = File.GetLastWriteTimeUtc(vanilla);
        await File.WriteAllTextAsync(vanilla, "a\nb\nC\n");
        File.SetLastWriteTimeUtc(vanilla, time.AddMinutes(1));

        var second = engine.Plan(loadout, MergeSourceMode.Apply);
        second.Should().NotBeSameAs(first);
        AsText(second.Single()).Should().Be("A\nb\nc\n", "against the new copy B's line 3 is the original and A's change back to c is the edit");
    }

    [Fact]
    public async Task TwoModsWithTheSameNameAreTwoContributorsNamedByTheirFolders()
    {
        var loadout = await CreateLoadout();
        await WriteVanilla(X, Vanilla);
        await Mod(loadout, "Foo", "modA", 2, Text(X, "a\nB1\nc\n"));
        await Mod(loadout, "Foo", "modB", 1, Text(X, "a\nB2\nc\n"));
        loadout = Refresh(loadout);
        var engine = Engine();

        var output = engine.Plan(loadout, MergeSourceMode.Apply).Should().ContainSingle().Subject;

        output.Contributors.Should().Equal("Foo (modA)", "Foo (modB)");
        output.Notes.OfType<LinesKept>().Should().ContainSingle().Which.Should().Match<LinesKept>(n => n.KeptMod == "Foo (modA)" && n.DroppedMod == "Foo (modB)");
        engine.Report(loadout, MergeSourceMode.Report).Entries.Single().Contributors.Should().Equal("Foo (modA)", "Foo (modB)");
    }

    private async Task<Loadout.ReadOnly> WithBinaryContributor()
    {
        var loadout = await CreateLoadout();
        await WriteVanilla(X, Vanilla);
        await Mod(loadout, "A", "modA", 2, Text(X, "a\nB1\nc\n"), ("scripts/bad.txt", [0xFF, 0xFE]));
        await Mod(loadout, "B", "modB", 1, Text(X, "a\nb\nC\n"), Text("scripts/bad.txt", "text\n"));
        return Refresh(loadout);
    }

    [Fact]
    public async Task ABinaryContributorIsNotMergeableAndTheOtherPathsStillMerge()
    {
        var loadout = await WithBinaryContributor();
        var engine = Engine();

        var report = engine.Report(loadout, MergeSourceMode.Report);

        report.Entries.Select(e => (e.Path, e.Outcome)).Should().Equal(
            ("scripts/bad.txt", MergeReportOutcome.NotMergeable),
            (X, MergeReportOutcome.Merged));
        report.Entries[0].Reason.Should().Contain("not UTF-8");
        report.Refusal.Should().BeNull();
        engine.Plan(loadout, MergeSourceMode.Apply).Select(o => o.RelPath).Should().Equal(X);
    }

    [Fact]
    public async Task AContestedFileTheGameDoesNotMergeIsReportedNotMergeable()
    {
        var loadout = await CreateLoadout();
        await Mod(loadout, "A", "modA", 2, ("x.bin", [1]));
        await Mod(loadout, "B", "modB", 1, ("x.bin", [2]));

        var entry = Engine().Report(Refresh(loadout), MergeSourceMode.Report).Entries.Should().ContainSingle().Subject;

        entry.Outcome.Should().Be(MergeReportOutcome.NotMergeable);
        entry.Reason.Should().Be("not a file the app merges; load order decides");
        entry.Contributors.Should().Equal("A", "B");
    }

    [Fact]
    public async Task ThreeModsFoldHighestFirst()
    {
        var loadout = await CreateLoadout();
        await WriteVanilla(X, Vanilla);
        await Mod(loadout, "A", "modA", 3, Text(X, "A\nb\nc\n"));
        await Mod(loadout, "B", "modB", 2, Text(X, "a\nB\nc\n"));
        await Mod(loadout, "C", "modC", 1, Text(X, "a\nb\nC\n"));

        var output = Engine().Plan(Refresh(loadout), MergeSourceMode.Apply).Single();

        AsText(output).Should().Be("A\nB\nC\n");
        output.Notes.Should().BeEmpty();
        output.Contributors.Should().Equal("A", "B", "C");
    }

    [Fact]
    public async Task ALossBetweenTwoLowerModsNamesTheModWhoseLinesWereKept()
    {
        var loadout = await CreateLoadout();
        await WriteVanilla(X, Vanilla);
        await Mod(loadout, "A", "modA", 3, Text(X, "A\nb\nc\n"));
        await Mod(loadout, "B", "modB", 2, Text(X, "a\nB\nc\n"));
        await Mod(loadout, "C", "modC", 1, Text(X, "a\nC\nc\n"));

        var output = Engine().Plan(Refresh(loadout), MergeSourceMode.Apply).Single();

        AsText(output).Should().Be("A\nB\nc\n");
        var note = output.Notes.Should().ContainSingle().Subject.Should().BeOfType<LinesKept>().Subject;
        note.Should().Be(new LinesKept(X, 2, 2, "B", "C"));
        note.Loss.Should().Be(new MergeLoss("C", "B"));
        output.Notes.Select(n => n.Describe()).Should().NotContain(d => d.Contains("A") || d.Contains(MergeNames.PreferredMod));
    }

    [Fact]
    public async Task PlannedOutputsListsEveryContestedPathWithAStrategyWithoutReadingBytes()
    {
        var loadout = await CreateLoadout();
        await Mod(loadout, "A", "modA", 2, Text(X, "one\n"), ("x.bin", [1]));
        await Mod(loadout, "B", "modB", 1, Text(X, "two\n"), ("x.bin", [2]));
        var store = Substitute.For<IFileStore>();
        var trust = Substitute.For<IVanillaTrust>();
        var engine = new MergeEngine(store, trust, NullLogger<MergeEngine>.Instance, _ => _game);

        var planned = engine.PlannedOutputs(Refresh(loadout));

        planned.Should().Equal(new PlannedOutput(new GamePath(LocationId.Game, "Mods/merged/scripts/x.txt"), X));
        store.ReceivedCalls().Should().BeEmpty();
        trust.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task APathWhoseLastMergeFailedLeavesPlannedOutputs()
    {
        var loadout = await WithBinaryContributor();
        var engine = Engine();

        engine.PlannedOutputs(loadout).Select(p => p.RelPath).Should().Equal("scripts/bad.txt", X);

        engine.Plan(loadout, MergeSourceMode.Apply);

        engine.PlannedOutputs(loadout).Select(p => p.RelPath).Should().Equal(X);
    }

    [Fact]
    public async Task TheReportEndsWithTheGamesReorderHint()
    {
        var (loadout, _, _) = await OverlappingEdits();

        Engine().Report(loadout, MergeSourceMode.Report).PriorityOrderHint.Should().EndWith("on the Test Order page, the mod loaded last wins");
    }

    [Fact]
    public async Task MergedBytesIsThePlannedFileFoundByRelativePathIgnoringCase()
    {
        var (loadout, _, _) = await OverlappingEdits();
        var engine = Engine();

        Encoding.UTF8.GetString(engine.MergedBytes(loadout, "Scripts/X.TXT")!).Should().Be("a\nB1\nc\n");
        engine.MergedBytes(loadout, "scripts/other.txt").Should().BeNull();
    }

    [Fact]
    public async Task APathTheGameSaysIsNoContestIsNotInTheReportButCanBeAskedFor()
    {
        var loadout = await CreateLoadout();
        await Mod(loadout, "A", "modA", 2, ("content/blob0.bundle", [1]));
        await Mod(loadout, "B", "modB", 1, ("content/blob0.bundle", [2]));
        loadout = Refresh(loadout);
        var engine = Engine();

        engine.Report(loadout, MergeSourceMode.Report).Entries.Should().BeEmpty();
        engine.PlannedOutputs(loadout).Should().BeEmpty();
        var ignored = engine.NotContested(loadout).Should().ContainSingle().Subject;
        ignored.Rel.Should().Be("content/blob0.bundle");
        ignored.Contributors.Select(c => c.ModName).Should().Equal("A", "B");
    }

    [Fact]
    public void ServesOnlyGamesThatHaveAMergeGame()
    {
        var engine = Engine();

        engine.Serves(Substitute.For<IGameData>()).Should().BeFalse();
        engine.Serves(Substitute.For<IGameData, IHasMergeGame>()).Should().BeTrue();
    }

    [Fact]
    public void TheTestHostsGraphResolvesTheEngineOnceTheTextEngineIsAdded()
    {
        ServiceProvider.GetServices<IMergeEngine>().Should().ContainSingle(e => e is MergeEngine);
    }

    [Fact]
    public async Task AContributorWhoseBlobIsMissingFromTheStoreIsNotMergeableAndTheOtherPathsStillMerge()
    {
        var loadout = await CreateLoadout();
        await WriteVanilla(X, Vanilla);
        await Mod(loadout, "A", "modA", 2, Text(X, "a\nB1\nc\n"));
        await Mod(loadout, "B", "modB", 1, Text(X, "a\nb\nC\n"));
        // Rows only: the blob is never backed up, as when the archive was garbage collected.
        using (var tx = Connection.BeginTransaction())
        {
            var group = AddEmptyGroup(tx, loadout.LoadoutId, "C");
            AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, "Mods/modC/scripts/lost.txt"), "gone", out _, out _);
            var other = AddEmptyGroup(tx, loadout.LoadoutId, "D");
            AddFile(tx, loadout.LoadoutId, other, new GamePath(LocationId.Game, "Mods/modD/scripts/lost.txt"), "gone too", out _, out _);
            _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = group, Priority = ConflictPriority.From(5) };
            await tx.Commit();
        }
        loadout = Refresh(loadout);
        var engine = Engine();

        var report = engine.Report(loadout, MergeSourceMode.Report);

        report.Entries.Single(e => e.Path == "scripts/lost.txt").Reason.Should().StartWith("the merge failed:");
        report.Entries.Single(e => e.Path == X).Outcome.Should().Be(MergeReportOutcome.Merged);
        engine.PlannedOutputs(loadout).Select(p => p.RelPath).Should().Equal(X);
    }

    [Fact]
    public async Task AVanillaFileThatCannotBeReadMakesThatPathNotMergeable()
    {
        // root reads any file, so the permission cannot be provoked there.
        Assert.SkipWhen(Environment.IsPrivilegedProcess, "a privileged process can read a file with no permissions");
        var loadout = await CreateLoadout();
        const string rel = "scripts/vanilla-without-permission.txt";
        await WriteVanilla(rel, Vanilla);
        var vanilla = GameInstallation.Locations[LocationId.Game].Path.Combine($"vanilla/{rel}").GetFullPath();
        await Mod(loadout, "A", "modA", 2, Text(rel, "a\n"));
        await Mod(loadout, "B", "modB", 1, Text(rel, "b\n"));
        File.SetUnixFileMode(vanilla, UnixFileMode.None);
        try
        {
            var entry = Engine().Report(Refresh(loadout), MergeSourceMode.Report).Entries.Should().ContainSingle().Subject;

            entry.Outcome.Should().Be(MergeReportOutcome.NotMergeable);
            entry.Reason.Should().Contain("could not be read");
        }
        finally
        {
            File.SetUnixFileMode(vanilla, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task AVanillaPathThatIsAFolderIsTreatedAsNoVanillaCopy()
    {
        var loadout = await CreateLoadout();
        const string rel = "scripts/vanilla-is-a-folder.txt";
        GameInstallation.Locations[LocationId.Game].Path.Combine($"vanilla/{rel}").CreateDirectory();
        await Mod(loadout, "A", "modA", 2, Text(rel, "a\n"));
        await Mod(loadout, "B", "modB", 1, Text(rel, "b\n"));

        var entry = Engine().Report(Refresh(loadout), MergeSourceMode.Report).Entries.Should().ContainSingle().Subject;

        entry.Outcome.Should().Be(MergeReportOutcome.Merged);
        entry.VanillaSource.Should().Contain("no vanilla copy");
    }
}
