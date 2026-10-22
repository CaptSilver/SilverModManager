using FluentAssertions;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Formats;
using NexusMods.Games.FromSoft.Merge;
using NexusMods.Games.FromSoft.Tests.Fixtures;
using NexusMods.Games.FromSoft.Tests.Formats;
using NexusMods.Games.FromSoft.Tests.Formats.Fixtures;
using EsdFormat = NexusMods.Games.FromSoft.Formats.Esd;
using static NexusMods.Games.FromSoft.Tests.Merge.EsdMergeFixture;

namespace NexusMods.Games.FromSoft.Tests.Merge;

/// <summary>Every rule is run for a BND4 game and a BND3 game: the merge reads and rebuilds through the game's binder.</summary>
public class EsdArchiveMergeTests
{
    public static TheoryData<string> Games => ["Elden Ring", "Test Souls"];

    private static FromSoftFormatTraits TraitsOf(string game) => game switch
    {
        "Elden Ring" => EldenRingGame.Definition.Formats,
        _ => TestSoulsGame.Definition.Formats,
    };

    /// <summary>A talk archive holding the given raw blobs under the given entry ids.</summary>
    private static byte[] Talkesdbnd(FromSoftFormatTraits traits, IReadOnlyDictionary<int, byte[]> entries)
    {
        var members = entries.OrderBy(e => e.Key).Select(e => (Id: e.Key, Name: $"t{e.Key:D9}.esd", Data: e.Value)).ToList();
        var binder = traits.Binder switch
        {
            BinderFormat.Bnd4 => Bnd4Fixture.Build(members),
            BinderFormat.Bnd3 => Bnd3Fixture.Build(members),
            var other => throw new NotSupportedException($"No fixture builds {other} archives."),
        };
        return Dcx.Write(binder, traits.PackedWrite);
    }

    private static Dictionary<int, byte[]> EntriesOf(byte[] archive, FromSoftFormatTraits traits) =>
        Binders.For(traits).Read(Dcx.Read(archive, traits)).ToDictionary(e => e.Id, e => e.Data);

    private static byte[] Blob(params (long GroupId, (long StateId, long? Target)[] States)[] groups) =>
        EsdFormat.Write(Archive(groups));

    // The machine both mods edit: base redirects state 0 to 2, other to 3 and adds state 3.
    private static readonly byte[] BothVanilla = Blob((24, [(0, 1), (1, null), (2, null)]));
    private static readonly byte[] BothBase = Blob((24, [(0, 2), (1, null), (2, null)]));
    private static readonly byte[] BothOther = Blob((24, [(0, 3), (1, null), (2, null), (3, null)]));

    private static EsdFile Read(byte[] blob) => EsdFormat.Read(blob);

    [Fact]
    public void ThreeWayNeedsTheVanillaSoItsSignatureTakesThreeArchives()
    {
        var method = typeof(EsdArchiveMerge).GetMethod(nameof(EsdArchiveMerge.ThreeWay), [
            typeof(ReadOnlySpan<byte>), typeof(ReadOnlySpan<byte>), typeof(ReadOnlySpan<byte>),
            typeof(FromSoftFormatTraits), typeof(ICollection<IMergeNote>)]);
        method.Should().NotBeNull();
    }

    [Theory, MemberData(nameof(Games))]
    public void TakesAnEntryOnlyOneSideChanged(string game)
    {
        var t = TraitsOf(game);
        var van = Blob((1, [(0, null)]));
        var changed = Blob((1, [(0, null), (1, 0)]));
        var vanilla = Talkesdbnd(t, new Dictionary<int, byte[]> { [0] = van, [6] = van });
        var @base = Talkesdbnd(t, new Dictionary<int, byte[]> { [0] = van, [6] = van });
        var other = Talkesdbnd(t, new Dictionary<int, byte[]> { [0] = van, [6] = changed });

        var entries = EntriesOf(EsdArchiveMerge.ThreeWay(@base, other, vanilla, t), t);

        entries[6].Should().Equal(changed);
        entries[0].Should().Equal(van);
    }

    [Theory, MemberData(nameof(Games))]
    public void RefusesAnEntryOnlyTheOtherSideHas(string game)
    {
        var t = TraitsOf(game);
        var van = Blob((1, [(0, null)]));
        var vanilla = Talkesdbnd(t, new Dictionary<int, byte[]> { [0] = van });
        var @base = Talkesdbnd(t, new Dictionary<int, byte[]> { [0] = van });
        var other = Talkesdbnd(t, new Dictionary<int, byte[]> { [0] = van, [9] = van });

        var act = () => EsdArchiveMerge.ThreeWay(@base, other, vanilla, t);

        act.Should().Throw<MergeException>().WithMessage("*9*");
    }

    [Theory, MemberData(nameof(Games))]
    public void RefusesAnEntryVanillaDoesNotHave(string game)
    {
        // Without vanilla to compare against, "only the other side moved" cannot be told from "base authored this".
        var t = TraitsOf(game);
        var van = Blob((1, [(0, null)]));
        var baseOnly = Blob((24, [(0, 1), (1, null)]));
        var otherOnly = Blob((24, [(0, 1), (1, null), (2, null)]));
        var vanilla = Talkesdbnd(t, new Dictionary<int, byte[]> { [0] = van });
        var @base = Talkesdbnd(t, new Dictionary<int, byte[]> { [0] = van, [6] = baseOnly });
        var other = Talkesdbnd(t, new Dictionary<int, byte[]> { [0] = van, [6] = otherOnly });

        var act = () => EsdArchiveMerge.ThreeWay(@base, other, vanilla, t);

        act.Should().Throw<MergeException>().WithMessage("*6*");
    }

    [Theory, MemberData(nameof(Games))]
    public void LeavesAnUntouchedEntryByteIdentical(string game)
    {
        // The entry's own bytes are the claim: comparing whole archives would pass for a second reason, the fixture
        // already being packed the way a rebuild packs it.
        var t = TraitsOf(game);
        var van = Blob((1, [(0, null)]));
        var archive = Talkesdbnd(t, new Dictionary<int, byte[]> { [0] = van });

        EntriesOf(EsdArchiveMerge.ThreeWay(archive, archive, archive, t), t)[0].Should().Equal(van);
    }

    [Theory, MemberData(nameof(Games))]
    public void RunsTheGraphMergeOnAnEntryBothSidesChanged(string game)
    {
        var t = TraitsOf(game);
        var vanilla = Talkesdbnd(t, new Dictionary<int, byte[]> { [6] = BothVanilla });
        var @base = Talkesdbnd(t, new Dictionary<int, byte[]> { [6] = BothBase });
        var other = Talkesdbnd(t, new Dictionary<int, byte[]> { [6] = BothOther });

        var merged = Read(EntriesOf(EsdArchiveMerge.ThreeWay(@base, other, vanilla, t), t)[6]);

        Targets(Find(GroupOf(merged, 24), 0)).Should().Equal([2L]);
    }

    [Theory, MemberData(nameof(Games))]
    public void WritesTheMergedGraphIntoTheEntry(string game)
    {
        // The graph merge can run and collect its notes while the container forgets to write the result, shipping
        // base's blob unchanged with the other mod's whole contribution gone.
        var t = TraitsOf(game);
        var vanilla = Talkesdbnd(t, new Dictionary<int, byte[]> { [6] = BothVanilla });
        var @base = Talkesdbnd(t, new Dictionary<int, byte[]> { [6] = BothBase });
        var other = Talkesdbnd(t, new Dictionary<int, byte[]> { [6] = BothOther });

        var shipped = EntriesOf(EsdArchiveMerge.ThreeWay(@base, other, vanilla, t), t)[6];

        shipped.Should().NotEqual(BothBase);
        GroupOf(Read(shipped), 24).States.Select(s => s.Id).Order().Should().Equal(0L, 1L, 2L, 3L);
    }

    [Theory, MemberData(nameof(Games))]
    public void ChecksTheBytesItIsAboutToShip(string game)
    {
        // The graph handed to the writer holds together; the file the writer produces does not.
        var t = TraitsOf(game);
        var vanilla = Talkesdbnd(t, new Dictionary<int, byte[]> { [6] = BothVanilla });
        var @base = Talkesdbnd(t, new Dictionary<int, byte[]> { [6] = BothBase });
        var other = Talkesdbnd(t, new Dictionary<int, byte[]> { [6] = BothOther });

        byte[] CorruptingWrite(EsdFile file)
        {
            var group = file.Groups[0];
            return EsdFormat.Write(file with { Groups = [group with { States = [.. group.States, group.States[1]] }] });
        }

        var act = () => EsdArchiveMerge.ThreeWay(@base, other, vanilla, t, null, CorruptingWrite);

        act.Should().Throw<EsdMergeException>().WithMessage("*more than one state*");
    }

    [Theory, MemberData(nameof(Games))]
    public void CollectsNotesWhenTheGraphMergeLeavesOne(string game)
    {
        var t = TraitsOf(game);
        var vanilla = Talkesdbnd(t, new Dictionary<int, byte[]> { [6] = BothVanilla });
        var @base = Talkesdbnd(t, new Dictionary<int, byte[]> { [6] = BothBase });
        var other = Talkesdbnd(t, new Dictionary<int, byte[]> { [6] = BothOther });
        var notes = new List<IMergeNote>();

        EsdArchiveMerge.ThreeWay(@base, other, vanilla, t, notes);

        // State 3 also arrives orphaned as a consequence; the conflict note is the one this pins.
        notes.OfType<EsdNote>().Should().Contain(n => n.Group == 24 && n.State == 0 && n.Reason == EsdNoteReason.BothChanged);
    }

    [Theory, MemberData(nameof(Games))]
    public void WithoutANotesListDoesNotRaise(string game)
    {
        var t = TraitsOf(game);
        var vanilla = Talkesdbnd(t, new Dictionary<int, byte[]> { [6] = BothVanilla });
        var @base = Talkesdbnd(t, new Dictionary<int, byte[]> { [6] = BothBase });
        var other = Talkesdbnd(t, new Dictionary<int, byte[]> { [6] = BothOther });

        var act = () => EsdArchiveMerge.ThreeWay(@base, other, vanilla, t);

        act.Should().NotThrow();
    }

    [Theory, MemberData(nameof(Games))]
    public void OutputUsesTheGamesPackedWriteFormat(string game)
    {
        var t = TraitsOf(game);
        var vanilla = Talkesdbnd(t, new Dictionary<int, byte[]> { [6] = BothVanilla });
        var @base = Talkesdbnd(t, new Dictionary<int, byte[]> { [6] = BothBase });
        var other = Talkesdbnd(t, new Dictionary<int, byte[]> { [6] = BothOther });

        var merged = EsdArchiveMerge.ThreeWay(@base, other, vanilla, t);

        merged.Should().Equal(Dcx.Write(Dcx.Read(merged, t), t.PackedWrite));
    }

    [Fact]
    public void NotesRenderToTheSentenceAndNotTheRecord()
    {
        IMergeNote note = new EsdNote(24, 0, EsdNoteReason.BothChanged);

        note.Describe().Should().Be("group 24: both mods changed vanilla state 0 -- kept the preferred mod's version");
        note.Describe().Should().NotContain(note.ToString()!);
    }

    [Fact]
    public void MatchesTheArchiveErmMerges()
    {
        // Entry 0 untouched, entry 6 changed by both, entry 7 changed by the other side only.
        var t = EldenRingGame.Definition.Formats;
        var untouched = Blob((1, [(0, null)]));
        var sevenVanilla = Blob((1, [(0, null)]));
        var sevenOther = Blob((1, [(0, null), (1, 0)]));
        var vanilla = Talkesdbnd(t, new Dictionary<int, byte[]> { [0] = untouched, [6] = BothVanilla, [7] = sevenVanilla });
        var @base = Talkesdbnd(t, new Dictionary<int, byte[]> { [0] = untouched, [6] = BothBase, [7] = sevenVanilla });
        var other = Talkesdbnd(t, new Dictionary<int, byte[]> { [0] = untouched, [6] = BothOther, [7] = sevenOther });
        var notes = new List<IMergeNote>();

        var merged = EsdArchiveMerge.ThreeWay(@base, other, vanilla, t, notes);

        Dcx.Read(merged, t).Should().Equal(GoldenFilesTests.Read("esd_archive_three_way.bnd"));
        notes.Select(n => n.Describe()).Should().Equal(
            File.ReadAllLines(Path.Combine(GoldenFilesTests.Folder, "esd_archive_three_way.notes.txt")));
    }

    /// <summary>
    /// Opt-in over the real archives of the two mods that edit the same machine: needs a checkout of the erm tool
    /// and the archives it pinned, and no mod or game file is checked in.
    /// </summary>
    [Fact]
    public void TheRealMergeShipsAFileThatReadsBackAndHoldsTogether()
    {
        const string talkBinder = "script/talk/m00_00_00_00.talkesdbnd.dcx";
        var t = EldenRingGame.Definition.Formats;
        var vanilla = ErmVendorArchive.ReadPinnedMember("item-enemy-randomizer", "randomizer/diste/Vanilla/m00_00_00_00.talkesdbnd.dcx");
        var bossRes = ErmVendorArchive.ReadPinnedMember("boss-resurrection-lite", talkBinder);
        var melina = ErmVendorArchive.ReadPinnedMember("journey-with-melina", talkBinder);

        var merged = EsdArchiveMerge.ThreeWay(bossRes, melina, vanilla, t);

        var machine = EsdFormat.Read(EntriesOf(merged, t)[0]);
        EsdCheck.Check(machine);
        machine.Groups.Select(g => g.Id).Should().Contain(2147482648L, "Melina's own machine comes across");
    }
}
