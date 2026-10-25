using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Games.FromSoft.Build;
using SilverModManager.Games.FromSoft.Heal;
using SilverModManager.Games.FromSoft.Tests.Build;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using System.Security.Cryptography;
using SilverModManager.Games.FromSoft.Tests.Saves;
using SilverModManager.Abstractions.Steam.Values;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Games.FromSoft.Tests.Heal;

public sealed class RegulationBaselinesTests : IDisposable
{
    private static readonly byte[] Current = "the 1.17 regulation"u8.ToArray();

    private readonly string _root = Directory.CreateTempSubdirectory("regulation-baselines").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Store => Path.Combine(_root, "baselines");

    private string GameRoot => Path.Combine(_root, "install");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));

    private RegulationBaselines Baselines(IManifestVouching? vouching = null) => new(() => FileSystem.Shared.FromUnsanitizedFullPath(Store), vouching, _time);

    private static string Sha1Hex(byte[] blob) => Convert.ToHexStringLower(SHA1.HashData(blob));

    /// <summary>Vouches for exactly the files in <paramref name="known"/>, each by the manifest paired with it.</summary>
    private sealed class FixedVouching(params (byte[] File, ulong Manifest)[] known) : IManifestVouching
    {
        public ManifestMatch? Find(GameInstallation installation, Sha1Value hash)
            => known.Where(k => Sha1Value.From(SHA1.HashData(k.File)) == hash)
                .Select(k => new ManifestMatch(ManifestId.From(k.Manifest), DepotId.From(1245621)))
                .FirstOrDefault();
    }

    /// <summary>A regulation the game's key opens, stamped <paramref name="version"/>.</summary>
    private static byte[] Real(string version, byte fill = 1)
        => RegulationFixture.BuildFromEntries([(1, "a.param", Enumerable.Repeat(fill, 16).ToArray())], version, EldenRingGame.Definition.Formats);

    private AbsolutePath Candidate(byte[] blob, string name = "candidate.bin")
    {
        var path = Path.Combine(_root, "candidates", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, blob);
        return FileSystem.Shared.FromUnsanitizedFullPath(path);
    }

    private void KeepValidated(string version, byte[] blob)
    {
        Directory.CreateDirectory(Store);
        var file = Path.Combine(Store, $"regulation-{version}.bin");
        File.WriteAllBytes(file, blob);
        File.WriteAllText(file + ".xxh3", blob.xxHash3().Value.ToString("x16"));
        File.WriteAllText(file + ".provenance", new RegulationProvenance(
            BaselineTier.Validated, BaselineSource.LibraryArchive, Sha1Hex(blob), HealFixtures.Sha(blob), blob.xxHash3().Value.ToString("x16"), _time.GetUtcNow()).ToJson());
    }

    [Fact]
    public void AValidatedKeepIsAnAncestorOnlyAndNeverEntersTheVouchedHashes()
    {
        var blob = Real("11611000", 7);
        var baselines = Baselines();

        var kept = baselines.KeepValidated("11611000", blob, baselines.ValidatedProvenance(blob, BaselineSource.LibraryArchive, "Rand.zip: Vanilla/regulation.bin", "layout matches"));

        File.ReadAllBytes(kept.GetFullPath()).Should().Equal(blob);
        var provenance = baselines.Provenance("11611000")!;
        provenance.Tier.Should().Be(BaselineTier.Validated);
        provenance.Source.Should().Be(BaselineSource.LibraryArchive);
        provenance.Validation.Should().Be("layout matches");
        baselines.Known().Should().BeEmpty();
        baselines.VouchedHashes().Should().BeEmpty();
    }

    [Fact]
    public void AValidatedKeepNeverReplacesACopyAlreadyKept()
    {
        var first = Real("11611000", 7);
        var second = Real("11611000", 8);
        var baselines = Baselines();
        baselines.KeepValidated("11611000", first, baselines.ValidatedProvenance(first, BaselineSource.LibraryArchive, "One.zip: regulation.bin", "ok"));

        var kept = baselines.KeepValidated("11611000", second, baselines.ValidatedProvenance(second, BaselineSource.LibraryArchive, "Two.zip: regulation.bin", "ok"));

        File.ReadAllBytes(kept.GetFullPath()).Should().Equal(first);
        baselines.Provenance("11611000")!.OriginalPath.Should().StartWith("One.zip");
    }

    [Fact]
    public void AValidatedKeepDoesNotClaimSteamWasAsked()
    {
        // Validation stands in for a manifest search instead of following one, so the sidecar must not read as if Steam
        // had been asked and said no.
        var blob = Real("11611000", 7);
        var baselines = Baselines(new FixedVouching());

        baselines.ValidatedProvenance(blob, BaselineSource.LibraryArchive, "Rand.zip: regulation.bin", "ok").ManifestMatched.Should().BeNull();
    }

    [Fact]
    public void AVouchedProvenanceIsNotKeptThroughTheValidatedPath()
    {
        var blob = Real("11611000", 7);
        var baselines = Baselines();
        var vouched = baselines.ValidatedProvenance(blob, BaselineSource.LibraryArchive, "x", "y") with { Tier = BaselineTier.Vouched };

        var act = () => baselines.KeepValidated("11611000", blob, vouched);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AdoptingCopiesTheGamesRegulationUnderItsVersion()
    {
        var game = HealFixtures.Game(GameRoot, Current);

        var kept = Baselines().Adopt(game, HealFixtures.Identity(Current));

        File.ReadAllBytes(kept.GetFullPath()).Should().Equal(Current);
        kept.FileName.Should().Be("regulation-11701000.bin");
        kept.Parent.GetFullPath().Should().Be(Store);
    }

    [Fact]
    public void AdoptingRefusesASecondFileUnderOneVersionAndKeepsTheFirst()
    {
        // Two different regulations carrying one build stamp. Copying over the first would let a tampered install
        // replace a baseline already trusted; handing it back would fold a merge onto bytes the game does not have.
        var baselines = Baselines();
        var game = HealFixtures.Game(GameRoot, "first"u8.ToArray());
        var kept = baselines.Adopt(game, HealFixtures.Identity("first"u8.ToArray()));
        HealFixtures.WriteRegulation(GameRoot, "second"u8.ToArray());

        var act = () => baselines.Adopt(game, HealFixtures.Identity("second"u8.ToArray()));

        act.Should().Throw<HealException>().WithMessage("*not the regulation*");
        File.ReadAllBytes(kept.GetFullPath()).Should().Equal("first"u8.ToArray());
    }

    [Fact]
    public void OldBaselinesAreKeptSoARebuildStaysReproducible()
    {
        var baselines = Baselines();
        var game = HealFixtures.Game(GameRoot, "1.16"u8.ToArray());
        baselines.Adopt(game, HealFixtures.Identity("1.16"u8.ToArray(), "11601000"));
        HealFixtures.WriteRegulation(GameRoot, "1.17"u8.ToArray());
        baselines.Adopt(game, HealFixtures.Identity("1.17"u8.ToArray()));

        Directory.EnumerateFiles(Store, "*.bin").Select(Path.GetFileName).Should().BeEquivalentTo("regulation-11601000.bin", "regulation-11701000.bin");
    }

    [Fact]
    public void AMissingGameRegulationIsRefusedNamingTheFile()
    {
        var game = HealFixtures.Game(GameRoot);

        var act = () => Baselines().Adopt(game, HealFixtures.Identity(Current));

        act.Should().Throw<HealException>().WithMessage("*regulation.bin*");
        Directory.Exists(Store).Should().BeFalse("nothing was adopted");
    }

    [Fact]
    public void AdoptingRefusesWhenTheFileChangedSinceItWasIdentified()
    {
        // The identity was read from this file moments earlier. Different bytes now mean something is writing to the
        // install, and the blob in hand is not the one that was judged safe to trust.
        var game = HealFixtures.Game(GameRoot, "not what was identified"u8.ToArray());

        var act = () => Baselines().Adopt(game, BuildIds.Make(regulationHash: new string('a', 64)));

        act.Should().Throw<HealException>().WithMessage("*changed between planning and adopting*");
        Directory.Exists(Store).Should().BeFalse();
    }

    [Fact]
    public void AdoptingAcceptsTheFileItWasIdentifiedFrom()
    {
        var game = HealFixtures.Game(GameRoot, Current);

        var kept = Baselines().Adopt(game, HealFixtures.Identity(Current));

        File.ReadAllBytes(kept.GetFullPath()).Should().Equal(Current);
    }

    [Fact]
    public void AnAlreadyAdoptedBaselineIsReturnedWithoutReadingTheInstall()
    {
        // Keyed by version and never overwritten: the kept copy is the answer, and the game file is not read to produce it.
        var baselines = Baselines();
        baselines.Adopt(HealFixtures.Game(GameRoot, Current), HealFixtures.Identity(Current));
        var withoutRegulation = HealFixtures.Game(Path.Combine(_root, "empty"));

        var kept = baselines.Adopt(withoutRegulation, HealFixtures.Identity(Current));

        File.ReadAllBytes(kept.GetFullPath()).Should().Equal(Current);
    }

    [Fact]
    public void AKeptBaselineThatIsNotTheInstalledFileIsRefused()
    {
        // A hand-placed or damaged copy under the installed version's name must not become the ancestor of a merge.
        Directory.CreateDirectory(Store);
        File.WriteAllBytes(Path.Combine(Store, "regulation-11701000.bin"), "someone else's"u8.ToArray());

        var act = () => Baselines().Adopt(HealFixtures.Game(GameRoot, Current), HealFixtures.Identity(Current));

        act.Should().Throw<HealException>().WithMessage("*regulation-11701000.bin*not the regulation*");
    }

    [Fact]
    public void AGameWithoutARegulationHashCannotAdopt()
    {
        var game = HealFixtures.Game(GameRoot, Current);
        var identity = new BuildIdentity("2.7.0.0", "1");

        var act = () => Baselines().Adopt(game, identity);

        act.Should().Throw<HealException>().WithMessage("*regulation*");
    }

    [Fact]
    public void FindOfAVersionNeverAdoptedIsNullAndCreatesNothing()
    {
        Baselines().Find("11701000").Should().BeNull();

        Directory.Exists(Store).Should().BeFalse("a lookup only lists a directory");
    }

    [Fact]
    public void FindReturnsTheKeptFile()
    {
        var baselines = Baselines();
        var kept = baselines.Adopt(HealFixtures.Game(GameRoot, Current), HealFixtures.Identity(Current));

        baselines.Find("11701000").Should().Be(kept);
        baselines.Find("11601000").Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("../11701000")]
    [InlineData("a/b")]
    [InlineData("11701000\0")]
    public void AVersionThatIsNotAFileNameIsRefusedRatherThanLookedUp(string version)
    {
        var act = () => Baselines().Find(version);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void KnownListsAnAdoptedVersionWithTheAdoptedFilesHash()
    {
        var baselines = Baselines();
        baselines.Adopt(HealFixtures.Game(GameRoot, Current), HealFixtures.Identity(Current));

        baselines.Known().Should().BeEquivalentTo(new Dictionary<string, Hash> { ["11701000"] = Current.xxHash3() });
    }

    [Fact]
    public void KnownReadsTheHashFromTheSidecarWrittenAtAdoption()
    {
        // Poisoning the sidecar shows the answer is read back and not recomputed from the file on every call.
        var baselines = Baselines();
        var kept = baselines.Adopt(HealFixtures.Game(GameRoot, Current), HealFixtures.Identity(Current));
        File.WriteAllText(kept.GetFullPath() + ".xxh3", Hash.From(42).Value.ToString("x16"));

        baselines.Known()["11701000"].Should().Be(Hash.From(42));
    }

    [Fact]
    public void KnownIsEmptyBeforeAnythingIsAdopted()
    {
        Baselines().Known().Should().BeEmpty();
        Directory.Exists(Store).Should().BeFalse();
    }

    [Fact]
    public void KnownHashesAKeptFileThatHasNoSidecar()
    {
        Directory.CreateDirectory(Store);
        File.WriteAllBytes(Path.Combine(Store, "regulation-11601000.bin"), "1.16"u8.ToArray());

        Baselines().Known().Should().BeEquivalentTo(new Dictionary<string, Hash> { ["11601000"] = "1.16"u8.ToArray().xxHash3() });
    }

    [Fact]
    public void AdoptingRecordsWhereTheFileCameFromAndAllThreeHashes()
    {
        var baselines = Baselines();

        var kept = baselines.Adopt(HealFixtures.Game(GameRoot, Current), HealFixtures.Identity(Current));

        var provenance = baselines.Provenance("11701000")!;
        provenance.Tier.Should().Be(BaselineTier.Vouched);
        provenance.Source.Should().Be(BaselineSource.InstalledGame);
        provenance.Sha1.Should().Be(Sha1Hex(Current));
        provenance.Sha256.Should().Be(HealFixtures.Sha(Current));
        provenance.Xxh3.Should().Be(Current.xxHash3().Value.ToString("x16"));
        provenance.RecordedAt.Should().Be(_time.GetUtcNow());
        provenance.ManifestMatched.Should().BeNull("no manifest source was asked");
        File.Exists(kept.GetFullPath() + ".provenance").Should().BeTrue();
    }

    [Fact]
    public void AdoptingAFileInASteamManifestRecordsTheManifest()
    {
        var baselines = Baselines(new FixedVouching((Current, 8950790353897081369)));

        baselines.Adopt(HealFixtures.Game(GameRoot, Current), HealFixtures.Identity(Current));

        var provenance = baselines.Provenance("11701000")!;
        provenance.Source.Should().Be(BaselineSource.SteamManifest);
        provenance.ManifestId.Should().Be(8950790353897081369);
        provenance.ManifestMatched.Should().BeTrue();
        provenance.Tier.Should().Be(BaselineTier.Vouched);
    }

    [Fact]
    public void AdoptingAFileNoManifestCarriesIsStillVouchedButRecordsThatNoneMatched()
    {
        // The install's own regulation is the game's file by definition; the sidecar says Steam could not confirm it,
        // so a later finding that the install was modified can tell which kept copies to doubt.
        var baselines = Baselines(new FixedVouching());

        baselines.Adopt(HealFixtures.Game(GameRoot, Current), HealFixtures.Identity(Current));

        var provenance = baselines.Provenance("11701000")!;
        provenance.Source.Should().Be(BaselineSource.InstalledGame);
        provenance.Tier.Should().Be(BaselineTier.Vouched);
        provenance.ManifestMatched.Should().BeFalse();
        provenance.ManifestId.Should().BeNull();
    }

    [Fact]
    public void ABaselineKeptBeforeProvenanceExistedIsVouchedAndGetsItsProvenanceWritten()
    {
        Directory.CreateDirectory(Store);
        var file = Path.Combine(Store, "regulation-11601000.bin");
        File.WriteAllBytes(file, Current);
        File.WriteAllText(file + ".xxh3", Current.xxHash3().Value.ToString("x16"));

        var baselines = Baselines();

        baselines.Known().Should().ContainKey("11601000");
        var provenance = baselines.Provenance("11601000")!;
        provenance.Tier.Should().Be(BaselineTier.Vouched);
        provenance.Source.Should().Be(BaselineSource.InstalledGame);
        provenance.Sha1.Should().Be(Sha1Hex(Current));
        File.Exists(file + ".provenance").Should().BeTrue("the next read finds it written");
    }

    [Fact]
    public void ProvenanceOfAVersionNeverKeptIsNull()
    {
        Baselines().Provenance("11601000").Should().BeNull();
    }

    [Fact]
    public void ACorruptProvenanceIsRefusedRatherThanTrustedOrIgnored()
    {
        Directory.CreateDirectory(Store);
        var file = Path.Combine(Store, "regulation-11601000.bin");
        File.WriteAllBytes(file, Current);
        File.WriteAllText(file + ".provenance", "{ not json");

        var act = () => Baselines().Known();

        act.Should().Throw<HealException>().WithMessage("*regulation-11601000.bin.provenance*");
    }

    [Fact]
    public void KnownAndVouchedHashesLeaveOutAValidatedBaseline()
    {
        var baselines = Baselines();
        baselines.Adopt(HealFixtures.Game(GameRoot, Current), HealFixtures.Identity(Current));
        KeepValidated("11601000", "guessed at"u8.ToArray());

        baselines.Known().Keys.Should().Equal("11701000");
        baselines.VouchedHashes().Should().BeEquivalentTo([Current.xxHash3()]);
    }

    [Fact]
    public void AValidatedBaselineStillAnswersFind()
    {
        KeepValidated("11601000", "guessed at"u8.ToArray());

        var baselines = Baselines();

        baselines.Find("11601000").Should().NotBeNull();
        baselines.Provenance("11601000")!.Tier.Should().Be(BaselineTier.Validated);
    }

    [Fact]
    public void AdoptingTheSameBytesAsAValidatedBaselineVouchesForIt()
    {
        KeepValidated("11701000", Current);
        var baselines = Baselines();

        baselines.Adopt(HealFixtures.Game(GameRoot, Current), HealFixtures.Identity(Current));

        var provenance = baselines.Provenance("11701000")!;
        provenance.Tier.Should().Be(BaselineTier.Vouched);
        provenance.Source.Should().Be(BaselineSource.InstalledGame);
        baselines.Known().Should().ContainKey("11701000");
    }

    [Fact]
    public void AdoptingReplacesADifferentValidatedBaselineAndSaysWhichOne()
    {
        var guessed = "guessed at"u8.ToArray();
        KeepValidated("11701000", guessed);
        var baselines = Baselines();

        var kept = baselines.Adopt(HealFixtures.Game(GameRoot, Current), HealFixtures.Identity(Current));

        File.ReadAllBytes(kept.GetFullPath()).Should().Equal(Current);
        var provenance = baselines.Provenance("11701000")!;
        provenance.Tier.Should().Be(BaselineTier.Vouched);
        provenance.Replaces.Should().Be(HealFixtures.Sha(guessed));
    }

    [Fact]
    public void ImportingAFileASteamManifestCarriesKeepsItVouched()
    {
        var blob = Real("11701000");
        var baselines = Baselines(new FixedVouching((blob, 8950790353897081369)));
        var file = Candidate(blob);

        var result = baselines.Import(HealFixtures.Game(GameRoot), file, null);

        result.Outcome.Should().Be(ImportOutcome.Kept);
        result.Version.Should().Be("11701000");
        File.ReadAllBytes(result.File.GetFullPath()).Should().Equal(blob);
        result.File.FileName.Should().Be("regulation-11701000.bin");
        var provenance = baselines.Provenance("11701000")!;
        provenance.Tier.Should().Be(BaselineTier.Vouched);
        provenance.Source.Should().Be(BaselineSource.SteamManifest);
        provenance.ManifestId.Should().Be(8950790353897081369);
        provenance.OriginalPath.Should().Be(file.GetFullPath());
        baselines.Known()["11701000"].Should().Be(blob.xxHash3());
        File.Exists(result.File.GetFullPath() + ".partial").Should().BeFalse();
    }

    [Fact]
    public void ImportingAFileNoManifestCarriesIsRefusedNamingItsHashAndKeepsNothing()
    {
        var blob = Real("11701000");
        var baselines = Baselines(new FixedVouching());

        var act = () => baselines.Import(HealFixtures.Game(GameRoot), Candidate(blob), null);

        act.Should().Throw<HealException>().WithMessage($"*{Sha1Hex(blob)}*Steam*");
        Directory.Exists(Store).Should().BeFalse();
    }

    [Fact]
    public void ImportingWithNoManifestSourceAtAllIsRefused()
    {
        var act = () => Baselines().Import(HealFixtures.Game(GameRoot), Candidate(Real("11701000")), null);

        act.Should().Throw<HealException>().WithMessage("*Steam*");
        Directory.Exists(Store).Should().BeFalse();
    }

    [Fact]
    public void ImportingAFileOfAnotherVersionThanExpectedIsRefused()
    {
        var blob = Real("11701000");
        var baselines = Baselines(new FixedVouching((blob, 1)));

        var act = () => baselines.Import(HealFixtures.Game(GameRoot), Candidate(blob), "11611000");

        act.Should().Throw<HealException>().WithMessage("*11701000*11611000*");
        Directory.Exists(Store).Should().BeFalse();
    }

    [Fact]
    public void ImportingAFileTheGamesKeyDoesNotOpenIsRefused()
    {
        var junk = "not a regulation at all, but a manifest lists it"u8.ToArray();
        var baselines = Baselines(new FixedVouching((junk, 1)));

        var act = () => baselines.Import(HealFixtures.Game(GameRoot), Candidate(junk), null);

        act.Should().Throw<HealException>().WithMessage("*key opens*");
        Directory.Exists(Store).Should().BeFalse();
    }

    [Fact]
    public void ImportingAFileThatDoesNotExistIsRefusedNamingIt()
    {
        var missing = FileSystem.Shared.FromUnsanitizedFullPath(Path.Combine(_root, "nope.bin"));

        var act = () => Baselines(new FixedVouching()).Import(HealFixtures.Game(GameRoot), missing, null);

        act.Should().Throw<HealException>().WithMessage("*nope.bin*");
    }

    [Fact]
    public void ImportingTheFileAlreadyKeptChangesNothing()
    {
        var blob = Real("11701000");
        var baselines = Baselines(new FixedVouching((blob, 1)));
        var first = baselines.Import(HealFixtures.Game(GameRoot), Candidate(blob), null);
        var recorded = baselines.Provenance("11701000");
        _time.Advance(TimeSpan.FromHours(1));

        var again = baselines.Import(HealFixtures.Game(GameRoot), Candidate(blob, "again.bin"), null);

        again.Outcome.Should().Be(ImportOutcome.AlreadyKept);
        again.File.Should().Be(first.File);
        baselines.Provenance("11701000").Should().Be(recorded);
    }

    [Fact]
    public void ImportingAVouchedFileReplacesAValidatedOneAndSaysWhichOne()
    {
        var guessed = Real("11701000", fill: 9);
        KeepValidated("11701000", guessed);
        var blob = Real("11701000");
        var baselines = Baselines(new FixedVouching((blob, 1)));

        var result = baselines.Import(HealFixtures.Game(GameRoot), Candidate(blob), null);

        result.Outcome.Should().Be(ImportOutcome.ReplacedValidated);
        File.ReadAllBytes(result.File.GetFullPath()).Should().Equal(blob);
        baselines.Provenance("11701000")!.Replaces.Should().Be(HealFixtures.Sha(guessed));
        baselines.Known().Should().ContainKey("11701000");
    }

    [Fact]
    public void ASecondVouchedFileUnderOneVersionIsKeptBesideTheFirst()
    {
        // A hotfix can ship a different regulation under the same build stamp. Both are the game's own, so neither is
        // refused; which one a merge compares against is decided where the contributors are known.
        var first = Real("11701000", fill: 1);
        var second = Real("11701000", fill: 2);
        var baselines = Baselines(new FixedVouching((first, 1), (second, 2)));
        baselines.Import(HealFixtures.Game(GameRoot), Candidate(first), null);

        var result = baselines.Import(HealFixtures.Game(GameRoot), Candidate(second, "second.bin"), null);

        result.Outcome.Should().Be(ImportOutcome.KeptAlongside);
        File.ReadAllBytes(baselines.Find("11701000")!.Value.GetFullPath()).Should().Equal(first, "the first stays the answer to Find");
        baselines.FindAll("11701000").Select(f => File.ReadAllBytes(f.GetFullPath())).Should().SatisfyRespectively(
            a => a.Should().Equal(first),
            b => b.Should().Equal(second));
        baselines.VouchedHashes().Should().BeEquivalentTo([first.xxHash3(), second.xxHash3()]);
        baselines.Known()["11701000"].Should().Be(first.xxHash3());
        Directory.EnumerateFiles(Store, "*.bin").Should().HaveCount(2);
    }

    [Fact]
    public void ImportingTheSecondFileAgainChangesNothing()
    {
        var first = Real("11701000", fill: 1);
        var second = Real("11701000", fill: 2);
        var baselines = Baselines(new FixedVouching((first, 1), (second, 2)));
        baselines.Import(HealFixtures.Game(GameRoot), Candidate(first), null);
        baselines.Import(HealFixtures.Game(GameRoot), Candidate(second, "second.bin"), null);

        var again = baselines.Import(HealFixtures.Game(GameRoot), Candidate(second, "third.bin"), null);

        again.Outcome.Should().Be(ImportOutcome.AlreadyKept);
        baselines.FindAll("11701000").Should().HaveCount(2);
    }

    [Fact]
    public void FindAllOfAVersionNeverKeptIsEmpty()
    {
        Baselines().FindAll("11701000").Should().BeEmpty();
    }

}

public class RegulationBaselinesServiceTests(ITestOutputHelper helper) : AEldenRingTest<RegulationBaselinesServiceTests>(helper)
{
    [Fact]
    public void TheStoreResolvesFromTheFromSoftServicesAndIsOneInstance()
    {
        var first = ServiceProvider.GetRequiredService<RegulationBaselines>();

        first.Should().BeSameAs(ServiceProvider.GetRequiredService<RegulationBaselines>());
        first.Known().Should().BeEmpty();
    }
}
