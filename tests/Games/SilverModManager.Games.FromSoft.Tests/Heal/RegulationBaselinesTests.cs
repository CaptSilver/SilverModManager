using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Games.FromSoft.Build;
using SilverModManager.Games.FromSoft.Heal;
using SilverModManager.Games.FromSoft.Tests.Build;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;

namespace SilverModManager.Games.FromSoft.Tests.Heal;

public sealed class RegulationBaselinesTests : IDisposable
{
    private static readonly byte[] Current = "the 1.17 regulation"u8.ToArray();

    private readonly string _root = Directory.CreateTempSubdirectory("regulation-baselines").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Store => Path.Combine(_root, "baselines");

    private string GameRoot => Path.Combine(_root, "install");

    private RegulationBaselines Baselines() => new(() => FileSystem.Shared.FromUnsanitizedFullPath(Store));

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
