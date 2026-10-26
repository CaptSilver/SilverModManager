using FluentAssertions;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

/// <summary>
/// An equipment part holds a texture archive, two models and two animation sets. The rules run for a BND4 game and a
/// BND3 game; only the Kraken cases are Elden Ring alone, because Test Souls reads DFLT only.
/// </summary>
public class BinderMemberMergeTests
{
    public static TheoryData<string> Games => ["Elden Ring", "Test Souls"];

    private const string Glow = "Weapon Glow";
    private const string Moveset = "moveset_modpack";
    private const string Third = "Third Mod";

    private const string Dir = @"N:\GR\data\INTERROOT_win64\parts\wp_a_0556\";
    private const string TpfName = Dir + "WP_A_0556.tpf";
    private const string FlverName = Dir + "WP_A_0556.flver";
    private const string Flver1Name = Dir + "WP_A_0556_1.flver";
    private const string AnibndName = Dir + "WP_A_0556.anibnd";
    private const string Anibnd1Name = Dir + "WP_A_0556_1.anibnd";

    private static FromSoftFormatTraits TraitsOf(string game) => game switch
    {
        "Elden Ring" => EldenRingGame.Definition.Formats,
        _ => TestSoulsGame.Definition.Formats,
    };

    private static byte[] Bytes(int seed, int length)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static byte[] TpfOf(byte[] t1, byte[]? t2 = null) =>
        TpfFixture.Build([("T1", t1, 0), ("T2", t2 ?? Bytes(12, 64), 0)]);

    // A nested binder: members are never read below the bundle, only compared as one blob.
    private static byte[] Anibnd(int seed, params int[] innerIds) =>
        Bnd4Fixture.Build([.. innerIds.Select(id => (id, $"a{id:D3}.hkx", Bytes(seed + id, 48)))]);

    private static IReadOnlyList<(int Id, string Name, byte[] Data)> Vanilla() =>
    [
        (100, TpfName, TpfOf(Bytes(11, 64))),
        (200, FlverName, Bytes(20, 300)),
        (201, Flver1Name, Bytes(21, 200)),
        (400, AnibndName, Anibnd(400, 1, 2, 3)),
        (401, Anibnd1Name, Anibnd(401, 1, 2, 3)),
    ];

    private static IReadOnlyList<(int Id, string Name, byte[] Data)> With(
        IReadOnlyList<(int Id, string Name, byte[] Data)> members, params (int Id, byte[] Data)[] changes) =>
        [.. members.Select(m => changes.Any(c => c.Id == m.Id) ? (m.Id, m.Name, changes.First(c => c.Id == m.Id).Data) : m)];

    private static IReadOnlyList<(int Id, string Name, byte[] Data)> GlowMembers() =>
        With(Vanilla(), (100, TpfOf(Bytes(111, 64))), (200, Bytes(120, 310)));

    private static IReadOnlyList<(int Id, string Name, byte[] Data)> MovesetMembers() =>
        With(Vanilla(), (200, Bytes(220, 290)), (400, Anibnd(1400, 7, 8)), (401, Anibnd(1401, 7, 8, 9)));

    private static byte[] Bundle(FromSoftFormatTraits traits, IReadOnlyList<(int Id, string Name, byte[] Data)> members, DcxKind dcx = DcxKind.Dflt) =>
        PartsbndFixture.Build(traits, members, dcx);

    private static Dictionary<int, byte[]> Read(byte[] bundle, FromSoftFormatTraits traits) =>
        Binders.For(traits).Read(Dcx.Read(bundle, traits)).ToDictionary(e => e.Id, e => e.Data);

    private static byte[] Merge(
        FromSoftFormatTraits traits, byte[] @base, byte[] other, byte[] vanilla, List<IMergeNote>? notes = null,
        string baseName = Glow, string otherName = Moveset) =>
        BinderMemberMerge.ThreeWay(@base, other, vanilla, traits, notes, baseName, otherName);

    [Theory]
    [MemberData(nameof(Games))]
    public void TheTexturesComeFromTheModThatChangedThemAndTheAnimationsFromTheOther(string game)
    {
        var traits = TraitsOf(game);
        var notes = new List<IMergeNote>();

        var merged = Merge(traits, Bundle(traits, GlowMembers()), Bundle(traits, MovesetMembers()), Bundle(traits, Vanilla()), notes);

        var members = Read(merged, traits);
        var glow = GlowMembers().ToDictionary(m => m.Id, m => m.Data);
        var moveset = MovesetMembers().ToDictionary(m => m.Id, m => m.Data);
        var vanilla = Vanilla().ToDictionary(m => m.Id, m => m.Data);
        members[100].Should().Equal(glow[100]);
        members[200].Should().Equal(glow[200], "both changed the main model, so the preferred mod's is kept");
        members[201].Should().Equal(vanilla[201]);
        members[400].Should().Equal(moveset[400]);
        members[401].Should().Equal(moveset[401]);
        notes.OfType<MemberTaken>().Should().BeEquivalentTo([new MemberTaken(AnibndName, Moveset), new MemberTaken(Anibnd1Name, Moveset)]);
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void AModelBothModsChangedIsKeptFromThePreferredModAndSaysSo(string game)
    {
        var traits = TraitsOf(game);
        var notes = new List<IMergeNote>();

        Merge(traits, Bundle(traits, GlowMembers()), Bundle(traits, MovesetMembers()), Bundle(traits, Vanilla()), notes);

        var kept = notes.OfType<MemberKept>().Should().ContainSingle().Subject;
        kept.Member.Should().Be(FlverName);
        kept.KeptMod.Should().Be(Glow);
        kept.DroppedMod.Should().Be(Moveset);
        kept.Loss.Should().Be(new MergeLoss(Moveset, Glow));
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void AModelLostByTheModWhoseAnimationsWereTakenSaysTheyNowComeFromDifferentMods(string game)
    {
        var traits = TraitsOf(game);
        var notes = new List<IMergeNote>();

        Merge(traits, Bundle(traits, GlowMembers()), Bundle(traits, MovesetMembers()), Bundle(traits, Vanilla()), notes);

        var kept = notes.OfType<MemberKept>().Single();
        kept.Details.Should().Contain("different mods").And.Contain(Glow).And.Contain(Moveset);
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void AModelConflictWithNoTakenAnimationsHasNoDifferentModsDetails(string game)
    {
        var traits = TraitsOf(game);
        var notes = new List<IMergeNote>();
        var other = With(Vanilla(), (200, Bytes(220, 290)));

        Merge(traits, Bundle(traits, GlowMembers()), Bundle(traits, other), Bundle(traits, Vanilla()), notes);

        notes.OfType<MemberKept>().Single().Details.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void AMemberOnlyTheOtherModChangedIsTakenAndNoted(string game)
    {
        var traits = TraitsOf(game);
        var notes = new List<IMergeNote>();
        var other = With(Vanilla(), (201, Bytes(230, 210)));

        var merged = Merge(traits, Bundle(traits, Vanilla()), Bundle(traits, other), Bundle(traits, Vanilla()), notes);

        Read(merged, traits)[201].Should().Equal(Bytes(230, 210));
        notes.Should().BeEquivalentTo<IMergeNote>([new MemberTaken(Flver1Name, Moveset)]);
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void AMemberOnlyTheBaseChangedStaysWithoutANote(string game)
    {
        var traits = TraitsOf(game);
        var notes = new List<IMergeNote>();

        var merged = Merge(traits, Bundle(traits, GlowMembers()), Bundle(traits, Vanilla()), Bundle(traits, Vanilla()), notes);

        Read(merged, traits)[200].Should().Equal(GlowMembers().First(m => m.Id == 200).Data);
        notes.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void AMemberBothModsChangedTheSameWayIsLeftAlone(string game)
    {
        var traits = TraitsOf(game);
        var notes = new List<IMergeNote>();
        var same = With(Vanilla(), (200, Bytes(300, 300)));

        Merge(traits, Bundle(traits, same), Bundle(traits, same), Bundle(traits, Vanilla()), notes);

        notes.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void TextureArchivesBothModsChangedAreMergedByTextureName(string game)
    {
        var traits = TraitsOf(game);
        var notes = new List<IMergeNote>();
        var @base = With(Vanilla(), (100, TpfOf(Bytes(111, 64))));
        var other = With(Vanilla(), (100, TpfOf(Bytes(11, 64), Bytes(112, 64))));

        var merged = Merge(traits, Bundle(traits, @base), Bundle(traits, other), Bundle(traits, Vanilla()), notes);

        var tpf = ReadTextures(Read(merged, traits)[100]);
        tpf.Select(t => t.Name).Should().Equal("T1", "T2");
        tpf[0].Data.Should().Equal(Bytes(111, 64));
        tpf[1].Data.Should().Equal(Bytes(112, 64));
        notes.Should().BeEmpty("each mod changed a different texture");
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void ATextureBothModsChangedKeepsThePreferredModsAndNamesTheModsInTheNote(string game)
    {
        var traits = TraitsOf(game);
        var notes = new List<IMergeNote>();
        var other = With(Vanilla(), (100, TpfOf(Bytes(113, 64))));

        var merged = Merge(traits, Bundle(traits, GlowMembers()), Bundle(traits, other), Bundle(traits, Vanilla()), notes);

        Read(merged, traits)[100].Should().Equal(GlowMembers().First(m => m.Id == 100).Data);
        notes.OfType<TextureKept>().Should().ContainSingle().Which.Should().Be(new TextureKept("T1", Glow, Moveset));
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void ATextureArchiveThatWillNotReadFallsBackToTheOpaqueRuleWhileOtherMembersStillMerge(string game)
    {
        var traits = TraitsOf(game);
        var notes = new List<IMergeNote>();
        var broken = With(GlowMembers(), (100, "not a tpf"u8.ToArray()));
        var other = With(MovesetMembers(), (100, "also not a tpf"u8.ToArray()));

        var merged = Merge(traits, Bundle(traits, broken), Bundle(traits, other), Bundle(traits, Vanilla()), notes);

        var members = Read(merged, traits);
        members[100].Should().Equal("not a tpf"u8.ToArray());
        members[400].Should().Equal(MovesetMembers().First(m => m.Id == 400).Data, "the animations merged cleanly beside the unreadable textures");
        var kept = notes.OfType<MemberKept>().Single(n => n.Member == TpfName);
        kept.KeptMod.Should().Be(Glow);
        kept.Details.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void ATextureArchiveWithARepeatedNameFallsBackToTheOpaqueRule(string game)
    {
        var traits = TraitsOf(game);
        var notes = new List<IMergeNote>();
        var repeated = TpfFixture.Build([("T1", Bytes(1, 64), 0), ("T1", Bytes(2, 64), 0)]);
        var @base = With(Vanilla(), (100, repeated));
        var other = With(Vanilla(), (100, TpfOf(Bytes(113, 64))));

        var merged = Merge(traits, Bundle(traits, @base), Bundle(traits, other), Bundle(traits, Vanilla()), notes);

        Read(merged, traits)[100].Should().Equal(repeated);
        notes.OfType<MemberKept>().Single().Details.Should().Contain("more than once");
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void BundlesWithDifferentMembersAreRefused(string game)
    {
        var traits = TraitsOf(game);
        var fewer = Vanilla().Where(m => m.Id != 401).ToArray();

        var act = () => Merge(traits, Bundle(traits, GlowMembers()), Bundle(traits, fewer), Bundle(traits, Vanilla()));

        act.Should().Throw<MergeException>().WithMessage("*the three bundles hold different entries*401*");
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void AMemberNamedDifferentlyUnderOneIdIsRefusedNamingItAndBothMods(string game)
    {
        var traits = TraitsOf(game);
        var renamed = Vanilla().Select(m => m.Id == 200 ? (m.Id, Dir + "WP_A_0557.flver", m.Data) : m).ToArray();

        var act = () => Merge(traits, Bundle(traits, GlowMembers()), Bundle(traits, renamed), Bundle(traits, Vanilla()));

        act.Should().Throw<MergeException>().WithMessage($"*200*WP_A_0556.flver*{Glow}*WP_A_0557.flver*{Moveset}*");
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void AMemberStoredWithDifferentFlagsIsRefusedNamingItAndBothMods(string game)
    {
        var traits = TraitsOf(game);
        byte[] Flags(byte second) => [0x02, second, 0x02, 0x02, 0x02];
        var other = PartsbndFixture.Build(traits, MovesetMembers(), entryFlags: Flags(0x06));

        var act = () => Merge(traits, Bundle(traits, GlowMembers()), other, Bundle(traits, Vanilla()));

        act.Should().Throw<MergeException>().WithMessage($"*{FlverName}*{Glow}*{Moveset}*");
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void ADuplicateIdIsRefusedInWordsNotWithAnArgumentException(string game)
    {
        var traits = TraitsOf(game);
        var duplicated = Vanilla().Append((200, Dir + "WP_A_0556_2.flver", Bytes(5, 40))).ToArray();

        var act = () => Merge(traits, Bundle(traits, duplicated), Bundle(traits, MovesetMembers()), Bundle(traits, Vanilla()));

        act.Should().Throw<MergeException>().WithMessage($"*{Glow}*200*more than once*");
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void NoReplacementReturnsTheBaseBytesUntouched(string game)
    {
        var traits = TraitsOf(game);
        var @base = Bundle(traits, GlowMembers());

        var merged = Merge(traits, @base, Bundle(traits, Vanilla()), Bundle(traits, Vanilla()));

        merged.Should().Equal(@base);
    }

    [Fact]
    public void AKrakenBaseWithNothingToTakeStaysKraken()
    {
        var traits = EldenRingGame.Definition.Formats;
        var @base = Bundle(traits, GlowMembers(), DcxKind.Kraken);

        var merged = Merge(traits, @base, Bundle(traits, Vanilla()), Bundle(traits, Vanilla(), DcxKind.Kraken));

        merged.Should().Equal(@base);
        merged[0x28..0x2C].Should().Equal("KRAK"u8.ToArray());
    }

    [Fact]
    public void AKrakenBaseWithAMemberToTakeIsWrittenAsTheGamesPackedFormat()
    {
        var traits = EldenRingGame.Definition.Formats;

        var merged = Merge(traits, Bundle(traits, GlowMembers(), DcxKind.Kraken), Bundle(traits, MovesetMembers()), Bundle(traits, Vanilla(), DcxKind.Kraken));

        merged[0x28..0x2C].Should().Equal("DFLT"u8.ToArray());
        Read(merged, traits)[400].Should().Equal(MovesetMembers().First(m => m.Id == 400).Data);
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void TheRebuiltBundleKeepsTheBasesLayoutAndIsWrittenInTheGamesPackedFormat(string game)
    {
        var traits = TraitsOf(game);
        var expected = With(GlowMembers(), (400, MovesetMembers().First(m => m.Id == 400).Data), (401, MovesetMembers().First(m => m.Id == 401).Data));

        var merged = Merge(traits, Bundle(traits, GlowMembers()), Bundle(traits, MovesetMembers()), Bundle(traits, Vanilla()));

        Dcx.Read(merged, traits).Should().Equal(PartsbndFixture.Binder(traits, expected), "header, order, flags and hash table are base's");
        merged.Should().Equal(Dcx.Write(PartsbndFixture.Binder(traits, expected), traits.PackedWrite));
        Binders.For(traits).Read(Dcx.Read(merged, traits)).Select(e => e.Id).Should().Equal(100, 200, 201, 400, 401);
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void ANestedBinderBothModsChangedIsKeptWholeWithoutReadingInsideIt(string game)
    {
        var traits = TraitsOf(game);
        var notes = new List<IMergeNote>();
        var @base = With(Vanilla(), (400, Anibnd(500, 1, 2)));
        var other = With(Vanilla(), (400, Anibnd(600, 7, 8, 9)));

        var merged = Merge(traits, Bundle(traits, @base), Bundle(traits, other), Bundle(traits, Vanilla()), notes);

        Read(merged, traits)[400].Should().Equal(Anibnd(500, 1, 2));
        notes.OfType<MemberKept>().Should().ContainSingle().Which.Member.Should().Be(AnibndName);
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void WhenALaterModConflictsOverAMemberAnEarlierModAloneChangedTheEarlierModIsNamedAsKept(string game)
    {
        // Fold order A > B > C: B changed the flver alone, then C changed it too.
        var traits = TraitsOf(game);
        var notes = new List<IMergeNote>();
        var vanilla = Bundle(traits, Vanilla());
        var a = Bundle(traits, Vanilla());
        var b = Bundle(traits, With(Vanilla(), (200, Bytes(701, 300))));
        var c = Bundle(traits, With(Vanilla(), (200, Bytes(702, 300))));

        var step1 = Merge(traits, a, b, vanilla, notes, baseName: Glow, otherName: Moveset);
        var step2 = Merge(traits, step1, c, vanilla, notes, baseName: Glow, otherName: Third);

        Read(step2, traits)[200].Should().Equal(Bytes(701, 300));
        notes.OfType<MemberKept>().Should().ContainSingle().Which.Should().BeEquivalentTo(new MemberKept(FlverName, Moveset, Third, null));
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void ATextureConflictInsideATakenArchiveNamesTheModThatSuppliedTheArchive(string game)
    {
        var traits = TraitsOf(game);
        var notes = new List<IMergeNote>();
        var vanilla = Bundle(traits, Vanilla());
        var b = Bundle(traits, With(Vanilla(), (100, TpfOf(Bytes(111, 64)))));
        var c = Bundle(traits, With(Vanilla(), (100, TpfOf(Bytes(113, 64), Bytes(114, 64)))));

        var step1 = Merge(traits, vanilla, b, vanilla, notes, baseName: Glow, otherName: Moveset);
        Merge(traits, step1, c, vanilla, notes, baseName: Glow, otherName: Third);

        notes.OfType<TextureKept>().Should().ContainSingle().Which.Should().Be(new TextureKept("T1", Moveset, Third));
    }

    [Fact]
    public void NotesAreOptional()
    {
        var traits = EldenRingGame.Definition.Formats;

        var merged = BinderMemberMerge.ThreeWay(Bundle(traits, GlowMembers()), Bundle(traits, MovesetMembers()), Bundle(traits, Vanilla()), traits);

        Read(merged, traits).Should().HaveCount(5);
    }

    private static List<Texture> ReadTextures(byte[] raw) => [.. SilverModManager.Games.FromSoft.Formats.Tpf.Read(raw).Textures];
}
