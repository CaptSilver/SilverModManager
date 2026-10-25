using FluentAssertions;
using SilverModManager.Games.FromSoft.Build;
using SilverModManager.Games.FromSoft.Heal;
using SilverModManager.Games.FromSoft.Tests.Build;
using NexusMods.Paths;
using SilverModManager.Abstractions.Steam.Values;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Games.FromSoft.Tests.Heal;

/// <summary>Keeping a copy of whichever build the app identifies, whatever the stamp says about how it got there.</summary>
public sealed class KeepBuildTests : IDisposable
{
    private static readonly byte[] Current = "the 1.17 regulation"u8.ToArray();

    private readonly string _root = Directory.CreateTempSubdirectory("keep-build").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Store => Path.Combine(_root, "baselines");

    private string GameRoot => Path.Combine(_root, "install");

    private RegulationBaselines Baselines(IManifestVouching? vouching = null)
        => new(() => FileSystem.Shared.FromUnsanitizedFullPath(Store), vouching);

    private sealed class Vouches(ulong manifest) : IManifestVouching
    {
        public ManifestMatch? Find(GameInstallation installation, Sha1Value hash) => new(ManifestId.From(manifest), DepotId.From(1245621));
    }

    [Fact]
    public void TheLiveBuildIsKeptOnceAndLeftAloneAfterwards()
    {
        var baselines = Baselines();
        var game = HealFixtures.Game(GameRoot, Current);
        var live = HealFixtures.Identity(Current);

        var first = baselines.KeepBuild(game, live, live);
        var second = baselines.KeepBuild(game, live, live);

        first.Should().NotBeNull();
        File.ReadAllBytes(first!.Value.GetFullPath()).Should().Equal(Current);
        second.Should().BeNull("a copy of that build is already kept");
    }

    [Fact]
    public void AnInstallWithNoStampIsKeptBecauseThereIsNothingToCallTamperedAgainst()
    {
        var game = HealFixtures.Game(GameRoot, Current);

        Baselines().KeepBuild(game, null, HealFixtures.Identity(Current)).Should().NotBeNull();
    }

    [Fact]
    public void ABuildThatMovedWithTheInstallIsKept()
    {
        var game = HealFixtures.Game(GameRoot, Current);
        var stamped = BuildIds.Make(exe: "2.6.0.0", steamBuildId: "1", regulationVersion: "11601000", regulationHash: new string('b', 64));

        Baselines().KeepBuild(game, stamped, HealFixtures.Identity(Current)).Should().NotBeNull();
    }

    [Fact]
    public void ARegulationThatMovedWhileTheInstallDidNotIsNotKept()
    {
        // Something wrote to the game's data: the file in the folder is not a build Steam shipped.
        var game = HealFixtures.Game(GameRoot, Current);
        var stamped = BuildIds.Make(regulationHash: new string('b', 64));

        Baselines().KeepBuild(game, stamped, HealFixtures.Identity(Current)).Should().BeNull();

        Directory.Exists(Store).Should().BeFalse();
    }

    [Fact]
    public void ABuildWithoutARegulationVersionKeepsNothing()
    {
        var game = HealFixtures.Game(GameRoot);

        Baselines().KeepBuild(game, null, new BuildIdentity("2.7.0.0", "1")).Should().BeNull();

        Directory.Exists(Store).Should().BeFalse();
    }

    [Fact]
    public void AHotfixUnderAnUnchangedStampIsKeptBesideTheFirstBuildOnce()
    {
        // A build stamp is eight bytes; a hotfix can ship other bytes under it, and that build is lost at the next update unless kept.
        var baselines = Baselines();
        var kept = baselines.Adopt(HealFixtures.Game(GameRoot, Current), HealFixtures.Identity(Current));
        var hotfix = "hotfixed"u8.ToArray();
        HealFixtures.WriteRegulation(GameRoot, hotfix);
        var game = HealFixtures.Game(GameRoot);

        var alongside = baselines.KeepBuild(game, null, HealFixtures.Identity(hotfix));
        var again = baselines.KeepBuild(game, null, HealFixtures.Identity(hotfix));

        File.ReadAllBytes(kept.GetFullPath()).Should().Equal(Current, "the first build is never overwritten");
        alongside.Should().NotBeNull();
        alongside!.Value.FileName.Should().StartWith("regulation-11701000@");
        File.ReadAllBytes(alongside.Value.GetFullPath()).Should().Equal(hotfix);
        baselines.FindAll("11701000").Should().Equal(kept, alongside.Value);
        baselines.Find("11701000").Should().Be(kept);
        again.Should().BeNull("that hotfix is already kept");
    }

    [Fact]
    public void AKeptBuildThatSteamsManifestCarriesIsRecordedAsSuch()
    {
        var baselines = Baselines(new Vouches(1925130620768035844));
        var game = HealFixtures.Game(GameRoot, Current);

        baselines.KeepBuild(game, null, HealFixtures.Identity(Current));

        var provenance = baselines.Provenance("11701000")!;
        provenance.Source.Should().Be(BaselineSource.SteamManifest);
        provenance.ManifestId.Should().Be(1925130620768035844);
    }
}
