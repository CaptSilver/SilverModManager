using System.Text;
using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Mapping;
using Placement = SilverModManager.Wabbajack.Install.InstallModlistJob.Placement;
using WriteState = SilverModManager.Wabbajack.Install.InstallModlistJob.WriteState;

namespace SilverModManager.Wabbajack.Tests.Install;

/// <summary>The install's reconciliation decisions, run on hand-built rows and placements.</summary>
public class InstallReconcilerTests
{
    private static readonly string Settings = Mo2Layout.ProfileSettingsGroup;

    private static GamePath Game(string path) => new(LocationId.Game, path);

    private static RelativePath Rel(string path) => RelativePath.FromUnsanitizedInput(path);

    private static FileIdentity Store(ulong value) => FileIdentity.XxHash3(Hash.From(value));

    private static EntityId Id(ulong value) => EntityId.From(value);

    private static GroupFiles Group(WriteState state, string name)
        => state.Files[name] = new GroupFiles();

    private static KnownFile Row(GroupFiles files, string path, ulong store, ulong id, FileIdentity? listHash, bool wanted = false, long size = 100)
        => files.Add(Game(path), Store(store), new KnownFile { Ids = { Id(id) }, ListHash = listHash, Wanted = wanted, Size = Size.FromLong(size) });

    private static Placement ModFile(int index, string folder, string file, FileIdentity hash)
        => new(index, new InlineFile(Rel($"mods/{folder}/{file}"), hash, 10, "blob"), Rel($"mods/{folder}/{file}"),
            new Mo2Target.ModFile(folder, Game(file)), folder, Game(file));

    private static Placement SettingsTweak(int index, string listName, byte[] bytes)
    {
        var path = IniTweakLayers.TweakPath(listName, "Skyrim.ini");
        return new Placement(index, new InlineFile(Rel("profiles/Default/initweaks.ini"), Xx64.Of(bytes), bytes.Length, "blob"),
            Rel("profiles/Default/initweaks.ini"), new Mo2Target.ProfileTweak(path), Settings, path);
    }

    private static readonly byte[] ListTweak = Encoding.UTF8.GetBytes("[Display]\nfMaxTime=0.1\n");

    [Fact]
    public void AnUneditedListTweakAtTheLoadoutPathIsRemovedWhenTheListMovesItsTweak()
    {
        var state = new WriteState();
        var files = Group(state, Settings);
        var oldPath = IniTweakLayers.LoadoutTweakPath("Skyrim.ini");
        // The list's initweaks.ini is an inline file: no library file holds its bytes, so only the recorded hash can say it is unedited.
        var oldRow = Row(files, oldPath.Path.ToString(), 1, 11, Xx64.Of(ListTweak));
        var newPlacement = SettingsTweak(0, "My List", ListTweak);
        var newRow = Row(files, newPlacement.Path!.Value.Path.ToString(), 2, 12, newPlacement.Directive.XxHash64, wanted: true);
        var storedBytes = new Dictionary<FileIdentity, byte[]> { [Store(1)] = ListTweak };

        var (stale, kept) = InstallReconciler.StaleRows(state, (_, identity) => Xx64.Of(storedBytes[identity]).Equals(oldRow.ListHash));

        stale.Should().Equal(Id(11));
        kept.Should().BeEmpty();
        newRow.Wanted.Should().BeTrue();
    }

    [Fact]
    public void AListTweakUnderAnOldListNameIsRemoved()
    {
        var state = new WriteState();
        var files = Group(state, Settings);
        Row(files, IniTweakLayers.TweakPath("Old Name", "Skyrim.ini").Path.ToString(), 1, 11, Xx64.From(5));
        Row(files, IniTweakLayers.TweakPath("My List", "Skyrim.ini").Path.ToString(), 2, 12, Xx64.From(6), wanted: true);

        var (stale, kept) = InstallReconciler.StaleRows(state, (_, _) => false);

        stale.Should().Equal(Id(11));
        kept.Should().BeEmpty("only a loadout-named tweak can be the user's");
    }

    [Fact]
    public void AnEditedTweakAtTheLoadoutPathIsKeptAsTheUsersAndListed()
    {
        var state = new WriteState();
        var files = Group(state, Settings);
        var loadoutPath = IniTweakLayers.LoadoutTweakPath("Skyrim.ini");
        Row(files, loadoutPath.Path.ToString(), 1, 11, Xx64.From(5));
        var other = Row(files, "meshes/stale.nif", 3, 13, Xx64.From(7));

        var (stale, kept) = InstallReconciler.StaleRows(state, (_, _) => false);

        stale.Should().Equal(Id(13));
        kept.Should().Equal(loadoutPath);
        other.Wanted.Should().BeFalse();
    }

    [Fact]
    public void AWantedFilesFurtherRowsAreDuplicatesAndStale()
    {
        var state = new WriteState();
        var files = Group(state, "A");
        var known = Row(files, "a.esp", 1, 11, Xx64.From(5), wanted: true);
        known.Ids.Add(Id(12));

        var (stale, _) = InstallReconciler.StaleRows(state, (_, _) => true);

        stale.Should().Equal(Id(12));
    }

    [Fact]
    public void AFolderEmptiedOnlyBecauseItsArchiveIsMissingIsNotGone()
    {
        var state = new WriteState();
        Group(state, "B");
        Group(state, Settings);
        var placements = new[] { ModFile(0, "B", "b.esp", Xx64.From(1)) };

        InstallReconciler.GoneFolders(state, placements).Should().BeEmpty();
    }

    [Fact]
    public void AFolderTheNewListNamesNowhereIsGone()
    {
        var state = new WriteState();
        Row(Group(state, "A"), "a.esp", 1, 11, Xx64.From(1));
        Row(Group(state, "C"), "c.esp", 2, 12, Xx64.From(2));
        Group(state, Settings);
        var placements = new[] { ModFile(0, "A", "a.esp", Xx64.From(1)) };

        InstallReconciler.GoneFolders(state, placements).Should().BeEquivalentTo(["C"]);
    }

    private const long MiB = 1 << 20;

    [Fact]
    public void AGoneFolderSharingHalfItsBytesWithANewFolderIsARename()
    {
        var state = new WriteState();
        var files = Group(state, "A");
        Row(files, "one.bin", 1, 11, Xx64.From(1), size: MiB);
        Row(files, "two.bin", 2, 12, Xx64.From(2), size: MiB);
        var placements = new[] { ModFile(0, "A SE", "one.bin", Xx64.From(1)), ModFile(1, "A SE", "three.bin", Xx64.From(3)) };
        var gone = InstallReconciler.GoneFolders(state, placements);

        InstallReconciler.RenamedFolders(state, placements, gone).Should().Equal([("A", "A SE")]);
    }

    [Fact]
    public void ASmallFolderBelowTheFloorIsNotARename()
    {
        var state = new WriteState();
        var files = Group(state, "A");
        Row(files, "one.bin", 1, 11, Xx64.From(1));
        Row(files, "two.bin", 2, 12, Xx64.From(2));
        var placements = new[] { ModFile(0, "A SE", "one.bin", Xx64.From(1)), ModFile(1, "A SE", "two.bin", Xx64.From(2)) };
        var gone = InstallReconciler.GoneFolders(state, placements);

        InstallReconciler.RenamedFolders(state, placements, gone).Should().BeEmpty("two small files shared are a coincidence");
    }

    [Fact]
    public void AFolderUnderTheShareThresholdIsNotARename()
    {
        var state = new WriteState();
        var files = Group(state, "A");
        Row(files, "one.bin", 1, 11, Xx64.From(1), size: MiB);
        Row(files, "two.bin", 2, 12, Xx64.From(2), size: 3 * MiB);
        var placements = new[] { ModFile(0, "A SE", "one.bin", Xx64.From(1)) };
        var gone = InstallReconciler.GoneFolders(state, placements);

        InstallReconciler.RenamedFolders(state, placements, gone).Should().BeEmpty();
    }

    [Fact]
    public void RenamesPairOneToOneBestFirst()
    {
        var state = new WriteState();
        var a = Group(state, "A");
        var b = Group(state, "B");
        for (ulong i = 1; i <= 10; i++)
        {
            Row(a, $"a{i}.bin", i, i, Xx64.From(i), size: 1000);
            Row(b, $"b{i}.bin", 100 + i, 100 + i, Xx64.From(100 + i), size: 1000);
        }

        // A would take X first if the pairing went by name: X holds 6 of A's 10 files (60%), while B has all of its files
        // in both X and Y (100%). Best first gives X to B, and A, left with nothing else, pairs with nothing.
        var placements = new List<Placement>();
        for (ulong i = 1; i <= 6; i++) placements.Add(ModFile((int)i, "X", $"a{i}.bin", Xx64.From(i)));
        for (ulong i = 1; i <= 10; i++)
        {
            placements.Add(ModFile((int)i + 10, "X", $"b{i}.bin", Xx64.From(100 + i)));
            placements.Add(ModFile((int)i + 20, "Y", $"b{i}.bin", Xx64.From(100 + i)));
        }
        var gone = InstallReconciler.GoneFolders(state, placements);

        gone.Should().BeEquivalentTo(["A", "B"]);
        InstallReconciler.RenamedFolders(state, placements, gone).Should().Equal([("B", "X")]);
    }

    [Fact]
    public void AKeptOriginalIsRetriedOnlyWhenNoRowAtItsPathMatchesTheNewDirective()
    {
        var state = new WriteState();
        var files = Group(state, "A");
        Row(files, "same.dds", 1, 11, Xx64.From(1));
        Row(files, "changed.dds", 2, 12, Xx64.From(2));
        var placements = new[] { ModFile(0, "A", "same.dds", Xx64.From(1)), ModFile(1, "A", "changed.dds", Xx64.From(99)) };
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { placements[0].To.ToString(), placements[1].To.ToString() };

        Placement[] Finished(bool updateMode) => InstallReconciler.FinishedDirectives(state, placements, kept, updateMode, _ => false, (_, _) => false, (_, _) => { });

        Finished(updateMode: false).Should().BeEmpty("a resume writes a kept original again");
        var update = Finished(updateMode: true);
        update.Should().ContainSingle().Which.Should().BeSameAs(placements[0], "its row still matches the list; the other one no longer does");
    }

    [Fact]
    public void AFinishedDirectiveIsMarkedWantedAndReported()
    {
        var state = new WriteState();
        var known = Row(Group(state, "A"), "a.esp", 1, 11, Xx64.From(1));
        var placement = ModFile(0, "A", "a.esp", Xx64.From(1));
        var reported = new List<(Placement, FileIdentity)>();

        var finished = InstallReconciler.FinishedDirectives(state, [placement], new HashSet<string>(), false, _ => false, (_, _) => false, (p, i) => reported.Add((p, i)));

        finished.Should().Equal(placement);
        known.Wanted.Should().BeTrue();
        reported.Should().Equal([(placement, Store(1))]);
    }

    [Fact]
    public void AFileWithoutARecordedListHashIsFinishedWhenItIsALibraryCopyOfTheDirective()
    {
        var state = new WriteState();
        Row(Group(state, "A"), "a.esp", 1, 11, listHash: null);
        var placement = ModFile(0, "A", "a.esp", Xx64.From(1));

        InstallReconciler.FinishedDirectives(state, [placement], new HashSet<string>(), false, _ => false, (_, identity) => identity.Equals(Store(1)), (_, _) => { })
            .Should().Equal(placement);
        InstallReconciler.FinishedDirectives(state, [placement], new HashSet<string>(), false, _ => false, (_, _) => false, (_, _) => { })
            .Should().BeEmpty();
    }

    [Fact]
    public void AGameFileWithNeitherRowNorSkipWasKeptByTheUser()
    {
        var state = new WriteState();
        Row(Group(state, "Game Folder Files"), "has-row.dll", 1, 11, Xx64.From(1));
        Group(state, Settings);

        Placement Root(int index, string file) => new(index, new InlineFile(Rel($"Stock Game/{file}"), Xx64.From((ulong)index), 1, "blob"), Rel($"Stock Game/{file}"),
            new Mo2Target.GameRootFile("Game Folder Files", Rel(file)), "Game Folder Files", Game(file));

        var placements = new[] { Root(0, "has-row.dll"), Root(1, "has-skip.dll"), Root(2, "kept.dll"), ModFile(3, "A", "a.esp", Xx64.From(9)) };
        var skips = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "stock game/has-skip.dll" };

        InstallReconciler.GameFilesKeptByUser(state, placements, skips).Should().BeEquivalentTo([Rel("kept.dll")]);
    }
}
