using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Abstractions.Games.Merge;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.Games.Witcher.Witcher3;
using SilverModManager.Games.Witcher.Witcher3.SortOrder;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Witcher.Tests.Witcher3.Merge;

/// <summary>
/// Two Mods folders that change the same invented script, merged by the shared engine on top of the Load Order. The
/// game's own copy is a baselined file in the test's game folder, so three-way merges run.
/// </summary>
public class Witcher3MergeThroughLoadOrderTests(ITestOutputHelper helper)
    : ALoadoutDiagnosticEmitterTest<Witcher3MergeThroughLoadOrderTests, Witcher3Game, EngineMergeDiagnosticsEmitter>(helper)
{
    private const string Player = "content/scripts/game/player.ws";
    private const string VanillaPlayer = "content/content0/scripts/game/player.ws";
    private const string MergedFolder = "mod0000_MergedFiles";

    private static readonly string[] VanillaLines =
    [
        "function Alpha()", "{", "  var a : int;", "  a = 1;", "  Log(a);", "", "  var b : int;", "  b = 2;", "  Log(b);", "}",
    ];

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddWitcherGames()
        .AddUniversalGameLocator<Witcher3Game>(new Version("4.0"), locationsFileSystem: IsolatedKnownFolders.Create);

    private static SortItemKey<string> Key(string folder) => new(folder.ToLowerInvariant());

    private static string Script(string eol, params (int Line, string Text)[] changes)
    {
        var lines = (string[])VanillaLines.Clone();
        foreach (var (line, text) in changes) lines[line - 1] = text;
        return string.Join(eol, lines) + eol;
    }

    private static byte[] Bytes(string text) => new UTF8Encoding(false).GetBytes(text);

    private static string Text(byte[] bytes) => new UTF8Encoding(false).GetString(bytes);

    private AbsolutePath GameFile(string path) => GameInstallation.Locations[LocationId.Game].Path.Combine(path);

    private IMergeEngine Engine => ServiceProvider.GetRequiredService<MergeEngines>().For(Game)
                                   ?? throw new InvalidOperationException("No merge engine serves The Witcher 3");

    private WitcherModSortOrderVariety Variety =>
        Game.SortOrderManager.GetSortOrderVarieties().OfType<WitcherModSortOrderVariety>().Single();

    private Loadout.ReadOnly Fresh(Loadout.ReadOnly loadout) => Loadout.Load(Connection.Db, loadout.LoadoutId);

    private ALoadoutSynchronizer WitcherSynchronizer => (ALoadoutSynchronizer)Game.Synchronizer;

    /// <summary>
    /// The game's copy of the script on disk and a baseline that lists it, with <paramref name="nonVanilla"/> paths
    /// the baseline found modified.
    /// </summary>
    private async Task WriteVanilla(params string[] nonVanilla)
    {
        var vanilla = Script("\n");
        var file = GameFile(VanillaPlayer);
        file.Parent.CreateDirectory();
        await file.WriteAllBytesAsync(Bytes(vanilla));

        var local = ((StubbedFileHasherService)ServiceProvider.GetRequiredService<IFileHashesService>()).LocalConnection;
        var baseline = await LocalBaselineRows.AddLocalBaseline(local, GameInstallation.LocatorResult.Store, GameInstallation.Locations[LocationId.Game].Path.ToString(),
            LocalBaselineKind.Snapshot, [(VanillaPlayer, vanilla)], nonVanilla);

        // A baseline vouches for the game's files only while it was built for the installed build's locator ids.
        using var tx = local.BeginTransaction();
        foreach (var id in GameInstallation.LocatorResult.LocatorIds) tx.Add(baseline, LocalBaseline.LocatorIds, id.Value);
        await tx.Commit();
    }

    private async Task<LoadoutItemGroupId> Mod(Loadout.ReadOnly loadout, string name, ulong priority, params (string Path, byte[] Bytes)[] files)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, name);
        var backups = new List<ArchivedFileEntry>();
        foreach (var (path, bytes) in files)
        {
            var hash = bytes.xxHash3();
            var size = Size.FromLong(bytes.Length);
            AddFileOfBytes(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, path), hash, size);
            backups.Add(new ArchivedFileEntry(new MemoryStreamFactory(path, new MemoryStream(bytes)), hash, size));
        }

        _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = group, Priority = ConflictPriority.From(priority) };
        var result = await tx.Commit();
        await FileStore.BackupFiles(backups);
        return LoadoutItemGroupId.From(result[group.Value]);
    }

    private static void AddFileOfBytes(ITransaction tx, LoadoutId loadoutId, LoadoutItemGroupId group, GamePath path, Hash hash, Size size)
    {
        _ = new LoadoutFile.New(tx, out var id)
        {
            LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, id)
            {
                LoadoutItem = new LoadoutItem.New(tx, id) { LoadoutId = loadoutId, ParentId = group, Name = path.Path },
                TargetPath = path.ToGamePathParentTuple(loadoutId),
            },
            Hash = hash,
            Size = size,
        };
    }

    private static (string Path, byte[] Bytes) Mine(string folder, string rel, string text) => ($"Mods/{folder}/{rel}", Bytes(text));

    /// <summary>modA (priority 1) and modB (priority 2) each change <see cref="Player"/> as given.</summary>
    private async Task<Loadout.ReadOnly> TwoMods(string a, string b)
    {
        await WriteVanilla();
        var loadout = await CreateLoadout();
        await Mod(loadout, "modA", 1, Mine("modA", Player, a));
        await Mod(loadout, "modB", 2, Mine("modB", Player, b));
        return Fresh(loadout);
    }

    private MergedOutput Merged(Loadout.ReadOnly loadout) =>
        Engine.Plan(Fresh(loadout), MergeSourceMode.Apply).Should().ContainSingle().Which;

    private MergeReportEntry OnlyEntry(Loadout.ReadOnly loadout) =>
        Engine.Report(Fresh(loadout), MergeSourceMode.Report).Entries.Should().ContainSingle().Which;

    private async Task<string[]> CardDetails(Loadout.ReadOnly loadout)
    {
        var writer = ServiceProvider.GetRequiredService<IDiagnosticWriter>();
        return (await GetAllDiagnostics(loadout.LoadoutId)).Select(card => card.FormatDetails(writer)).ToArray();
    }

    [Fact]
    public async Task TheFolderLoadedLastWins_AndTheLossIsReported()
    {
        var loadout = await TwoMods(Script("\n", (4, "  a = 11;")), Script("\n", (4, "  a = 12;")));

        Text(Merged(loadout).Bytes).Should().Be(Script("\n", (4, "  a = 12;")), "modB is ranked over modA");
        var entry = OnlyEntry(loadout);
        entry.Outcome.Should().Be(MergeReportOutcome.Merged);
        entry.Contributors.Should().Equal("modB", "modA");
        entry.Notes.Should().ContainSingle().Which.Should().Contain("kept modB's");
        entry.VanillaSource.Should().Be("the game's own copy");
    }

    [Fact]
    public async Task MovingTheLoserPastTheWinner_FlipsTheMergeWinner()
    {
        var loadout = await TwoMods(Script("\n", (4, "  a = 11;")), Script("\n", (4, "  a = 12;")));
        var id = await Variety.GetOrCreateSortOrderFor(loadout.LoadoutId, loadout.LoadoutId);

        await Variety.MoveItems(id, [Key("modA")], Key("modB"), TargetRelativePosition.BeforeTarget, Connection.Db);

        Text(Merged(loadout).Bytes).Should().Be(Script("\n", (4, "  a = 11;")), "modA now loads last");
        OnlyEntry(loadout).Contributors.Should().Equal("modA", "modB");
    }

    [Fact]
    public async Task TwoGroupsInOneFolder_RankAsTheFolder()
    {
        await WriteVanilla();
        var loadout = await CreateLoadout();
        await Mod(loadout, "SdlLow", 1, Mine("modSDL", Player, Script("\n", (4, "  a = 21;"))));
        await Mod(loadout, "SdlHigh", 3, Mine("ModSdl", "content/scripts/game/other.ws", "// only here\n"));
        await Mod(loadout, "modB", 2, Mine("modB", Player, Script("\n", (4, "  a = 12;"))));

        loadout = Fresh(loadout);

        Text(Merged(loadout).Bytes).Should().Be(Script("\n", (4, "  a = 21;")), "the folder holds a priority 3 group, which beats modB's 2");
        OnlyEntry(loadout).Contributors.Should().Equal("SdlLow", "modB");
    }

    [Fact]
    public async Task AFolderSwitchedOffOnThePage_NoLongerContributesToTheMerge()
    {
        var loadout = await TwoMods(Script("\n", (4, "  a = 11;")), Script("\n", (8, "  b = 22;")));
        var id = await Variety.GetOrCreateSortOrderFor(loadout.LoadoutId, loadout.LoadoutId);
        Merged(loadout).Contributors.Should().Equal("modB", "modA");

        await Variety.SetEnabled(id, Key("modA"), false);

        Engine.Plan(Fresh(loadout), MergeSourceMode.Apply).Should().BeEmpty("modA is not loaded, so its edits must not reach the merged copy");
        Engine.Report(Fresh(loadout), MergeSourceMode.Report).Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task AFolderSwitchedBackOn_ContributesAgain()
    {
        var loadout = await TwoMods(Script("\n", (4, "  a = 11;")), Script("\n", (8, "  b = 22;")));
        var id = await Variety.GetOrCreateSortOrderFor(loadout.LoadoutId, loadout.LoadoutId);
        await Variety.SetEnabled(id, Key("modA"), false);
        Engine.Plan(Fresh(loadout), MergeSourceMode.Apply).Should().BeEmpty();

        await Variety.SetEnabled(id, Key("modA"), true);

        Text(Merged(loadout).Bytes).Should().Be(Script("\n", (4, "  a = 11;"), (8, "  b = 22;")));
    }

    [Fact]
    public async Task NonOverlappingHunks_BothApply()
    {
        var loadout = await TwoMods(Script("\n", (4, "  a = 11;")), Script("\n", (8, "  b = 22;")));

        Text(Merged(loadout).Bytes).Should().Be(Script("\n", (4, "  a = 11;"), (8, "  b = 22;")));
        OnlyEntry(loadout).Notes.Should().BeEmpty();
    }

    [Fact]
    public async Task AModifiedVanilla_MakesTheMergeTwoWayWithANote()
    {
        await WriteVanilla(VanillaPlayer);
        var loadout = await CreateLoadout();
        await Mod(loadout, "modA", 1, Mine("modA", Player, Script("\n", (4, "  a = 11;"), (8, "  b = 21;"))));
        await Mod(loadout, "modB", 2, Mine("modB", Player, Script("\n", (4, "  a = 12;"))));

        loadout = Fresh(loadout);

        Text(Merged(loadout).Bytes).Should().Be(Script("\n", (4, "  a = 12;")), "without the vanilla copy modB wins every difference, modA's line 8 included");
        OnlyEntry(loadout).VanillaSource.Should().Contain("was not used as the base").And.Contain("listed as modified");
    }

    [Fact]
    public async Task AMissingVanilla_MakesTheMergeTwoWayWithANote()
    {
        var loadout = await CreateLoadout();
        await Mod(loadout, "modA", 1, Mine("modA", "content/scripts/game/new.ws", "// A\nfoo();\n"));
        await Mod(loadout, "modB", 2, Mine("modB", "content/scripts/game/new.ws", "// B\nfoo();\n"));

        loadout = Fresh(loadout);

        Text(Merged(loadout).Bytes).Should().Be("// B\nfoo();\n");
        OnlyEntry(loadout).VanillaSource.Should().Contain("no vanilla copy");
    }

    [Fact]
    public async Task CrlfContributor_KeepsItsEndingsAndMakesNoSpuriousHunk()
    {
        // modB writes CRLF endings and a byte order mark over an LF game script; modA is plain LF.
        var loadout = await TwoMods(Script("\n", (4, "  a = 11;")), "\uFEFF" + Script("\r\n", (8, "  b = 22;")));

        var merged = Merged(loadout).Bytes;

        merged.Should().Equal(Bytes("\uFEFF" + Script("\r\n", (4, "  a = 11;"), (8, "  b = 22;"))), "the winner's endings and mark are kept, and only the two real changes are hunks");
        OnlyEntry(loadout).Notes.Should().BeEmpty();
    }

    [Fact]
    public async Task PlannedOutputs_AreIntrinsicFiles()
    {
        var loadout = await TwoMods(Script("\n", (4, "  a = 11;")), Script("\n", (4, "  a = 12;")));

        WitcherSynchronizer.IntrinsicFiles(loadout).Keys.Should()
            .Contain(new GamePath(LocationId.Game, $"Mods/{MergedFolder}/{Player}"))
            .And.Contain(WitcherConstants.W3ModsSettings);
    }

    private async Task AddToExternalChanges(Loadout.ReadOnly loadout, string path, string text)
    {
        var overrides = LoadoutOverridesGroup.FindByOverridesFor(Connection.Db, loadout.Id).Single();
        using var tx = Connection.BeginTransaction();
        var bytes = Bytes(text);
        AddFileOfBytes(tx, loadout.LoadoutId, overrides.AsLoadoutItemGroup().LoadoutItemGroupId, new GamePath(LocationId.Game, path), bytes.xxHash3(), Size.FromLong(bytes.Length));
        await tx.Commit();
        await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(path, new MemoryStream(bytes)), bytes.xxHash3(), Size.FromLong(bytes.Length))]);
    }

    [Fact]
    public async Task AVortexMergedFolderInExternalChanges_IsNotAContributor()
    {
        var loadout = await TwoMods(Script("\n", (4, "  a = 11;")), Script("\n", (4, "  a = 12;")));
        await AddToExternalChanges(loadout, $"Mods/{MergedFolder}/{Player}", Script("\n", (4, "  a = 99;")));

        var entry = OnlyEntry(loadout);

        entry.Outcome.Should().Be(MergeReportOutcome.Merged);
        entry.Contributors.Should().Equal("modB", "modA");
        (await CardDetails(loadout)).Should().NotContain(details => details.Contains("could not be merged"));
    }

    [Fact]
    public async Task ALeftoverMergedFileInExternalChanges_DoesNotRefuseTheApply()
    {
        var loadout = await TwoMods(Script("\n", (4, "  a = 11;")), Script("\n", (4, "  a = 12;")));
        await AddToExternalChanges(loadout, $"Mods/{MergedFolder}/{Player}", Script("\n", (4, "  a = 99;")));
        var mergedPath = new GamePath(LocationId.Game, $"Mods/{MergedFolder}/{Player}");

        var tree = await Synchronizer.BuildSyncTree(Fresh(loadout));
        await Synchronizer.Synchronize(Fresh(loadout));

        tree[mergedPath].SourceItemType.Should().Be(LoadoutSourceItemType.Intrinsic, "an intrinsic file sits above the override file");
        Text(await GameFile($"Mods/{MergedFolder}/{Player}").ReadAllBytesAsync()).Should().Be(Script("\n", (4, "  a = 12;")));
    }

    [Theory]
    [InlineData("content/x.xml")]
    [InlineData("content/blob0.bundle")]
    public async Task LooseXmlAndBundles_AreNotMergeable(string rel)
    {
        var loadout = await CreateLoadout();
        await Mod(loadout, "modA", 1, Mine("modA", rel, "<a/>"));
        await Mod(loadout, "modB", 2, Mine("modB", rel, "<b/>"));
        loadout = Fresh(loadout);

        var entry = OnlyEntry(loadout);

        entry.Outcome.Should().Be(MergeReportOutcome.NotMergeable);
        Engine.Report(loadout, MergeSourceMode.Report).Refusal.Should().BeNull();
        var card = (await CardDetails(loadout)).Should().ContainSingle().Which;
        card.Should().Contain("Load Order page");
    }

    [Fact]
    public async Task TheLossHasACardNamingTheLoadOrderPage()
    {
        var loadout = await TwoMods(Script("\n", (4, "  a = 11;")), Script("\n", (4, "  a = 12;")));

        var card = (await CardDetails(loadout)).Should().ContainSingle().Which;

        card.Should().Contain("move modA below modB so it loads after it").And.Contain("Load Order page");
    }

    [Fact]
    public void TheMergesPage_IsOfferedForWitcher3()
    {
        ServiceProvider.GetRequiredService<MergeEngines>().For(Game).Should().NotBeNull();
    }

    [Fact]
    public void Witcher3_PassesTheMergeHookupCheck()
    {
        Game.Should().BeAssignableTo<IHasMergeGame>();
        var act = () => AMergeGame.CheckHookups(Game);

        act.Should().NotThrow();
    }

    [Fact]
    public async Task AFailingMerge_IsReportedWithoutRefusingTheApply()
    {
        // modA's copy is not text, so its merge fails and the path is left to load order.
        await WriteVanilla();
        var loadout = await CreateLoadout();
        await Mod(loadout, "modA", 1, ($"Mods/modA/{Player}", [0xFF, 0xFE, 0x00]));
        await Mod(loadout, "modB", 2, Mine("modB", Player, Script("\n", (4, "  a = 12;"))));

        await Synchronizer.Synchronize(Fresh(loadout));
        await Synchronizer.Synchronize(Fresh(loadout));

        var entry = OnlyEntry(loadout);
        entry.Outcome.Should().Be(MergeReportOutcome.NotMergeable);
        entry.Reason.Should().Contain("the merge failed");
        GameFile($"Mods/{MergedFolder}/{Player}").FileExists.Should().BeFalse();
        GameFile($"Mods/modB/{Player}").FileExists.Should().BeTrue("each folder's own copy still deploys");
    }
}
