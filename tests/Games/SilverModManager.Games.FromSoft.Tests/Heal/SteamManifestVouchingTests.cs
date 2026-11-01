using SilverModManager.Abstractions.Steam;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Steam.Values;
using SilverModManager.Games.FromSoft.Heal;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.FromSoft.Tests.Heal;

public sealed class SteamManifestVouchingTests : IDisposable
{
    private static readonly byte[] Regulation = "the shipped regulation"u8.ToArray();
    private static readonly byte[] Other = "something else"u8.ToArray();
    private const uint Depot = 1245621;

    private readonly string _root = Directory.CreateTempSubdirectory("manifest-vouching").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private AbsolutePath SteamRoot => FileSystem.Shared.FromUnsanitizedFullPath(Path.Combine(_root, "steam"));

    private GameInstallation Install(params ulong[] manifestIds) => HealFixtures.Game(Path.Combine(_root, "game"), storeRoot: SteamRoot, manifestIds: manifestIds);

    private static SteamManifestVouching Vouching(FakeManifestSource source) => new(source, NullLogger<SteamManifestVouching>.Instance);

    [Fact]
    public void AFileInTheInstalledManifestIsVouchedForByThatManifest()
    {
        var source = new FakeManifestSource();
        source.Add(Depot, 200, files: [Regulation, Other]);

        var match = Vouching(source).Find(Install(200), FakeManifestSource.Sha1(Regulation));

        match.Should().Be(new ManifestMatch(ManifestId.From(200), DepotId.From(Depot)));
    }

    [Fact]
    public void AFileNoManifestCarriesIsNotVouchedFor()
    {
        var source = new FakeManifestSource();
        source.Add(Depot, 200, files: [Other]);

        Vouching(source).Find(Install(200), FakeManifestSource.Sha1(Regulation)).Should().BeNull();
    }

    [Fact]
    public void AnOlderCachedManifestOfTheSameDepotVouchesForAnOlderBuild()
    {
        // Steam keeps the manifests of builds that are no longer installed beside the current one.
        var source = new FakeManifestCatalog();
        source.Add(Depot, 200, files: [Other]);
        source.Add(Depot, 100, files: [Regulation]);

        var match = Vouching(source).Find(Install(200), FakeManifestSource.Sha1(Regulation));

        match.Should().Be(new ManifestMatch(ManifestId.From(100), DepotId.From(Depot)));
    }

    [Fact]
    public void TheInstalledManifestIsPreferredWhenAnOlderOneCarriesTheSameHash()
    {
        var source = new FakeManifestCatalog();
        source.Add(Depot, 100, files: [Regulation]);
        source.Add(Depot, 200, files: [Regulation]);

        Vouching(source).Find(Install(200), FakeManifestSource.Sha1(Regulation))!.ManifestId.Should().Be(ManifestId.From(200));
    }

    [Fact]
    public void AnotherDepotsManifestsAreNotSearched()
    {
        var source = new FakeManifestCatalog();
        source.Add(Depot, 200, files: [Other]);
        source.Add(99, 300, files: [Regulation]);

        Vouching(source).Find(Install(200), FakeManifestSource.Sha1(Regulation)).Should().BeNull();
        source.Reads.Should().NotContain(ManifestId.From(300));
    }

    [Fact]
    public void EncryptedFileNamesDoNotStopTheHashesBeingUsed()
    {
        var source = new FakeManifestCatalog();
        source.Add(Depot, 200, encryptedNames: true, files: [Regulation]);

        Vouching(source).Find(Install(200), FakeManifestSource.Sha1(Regulation))!.ManifestId.Should().Be(ManifestId.From(200));
    }

    [Fact]
    public void ASourceThatCannotListStillSearchesTheInstalledManifests()
    {
        var source = new FakeManifestSource();
        source.Add(Depot, 200, files: [Regulation]);
        source.Add(Depot, 100, files: [Other]);

        Vouching(source).Find(Install(200), FakeManifestSource.Sha1(Regulation))!.ManifestId.Should().Be(ManifestId.From(200));
    }

    [Fact]
    public void ASourceThatCannotListAndNeedsTheDepotKeyVouchesForNothing()
    {
        var source = new FakeManifestSource();
        source.Add(Depot, 200, encryptedNames: true, files: [Regulation]);

        Vouching(source).Find(Install(200), FakeManifestSource.Sha1(Regulation)).Should().BeNull();
    }

    [Fact]
    public void AManifestSteamHasNotCachedIsSkipped()
    {
        var source = new FakeManifestSource();
        source.Add(Depot, 200, files: [Regulation]);

        Vouching(source).Find(Install(555, 200), FakeManifestSource.Sha1(Regulation))!.ManifestId.Should().Be(ManifestId.From(200));
    }

    [Fact]
    public void AnInstallWithoutASteamClientRootHasNothingToVouchWith()
    {
        var source = new FakeManifestSource();
        source.Add(Depot, 200, files: [Regulation]);
        var noRoot = HealFixtures.Game(Path.Combine(_root, "game"), manifestIds: [200UL]);

        Vouching(source).Find(noRoot, FakeManifestSource.Sha1(Regulation)).Should().BeNull();
    }

    [Fact]
    public void WithoutAManifestSourceNothingIsVouchedFor()
    {
        new SteamManifestVouching(null, NullLogger<SteamManifestVouching>.Instance)
            .Find(Install(200), FakeManifestSource.Sha1(Regulation)).Should().BeNull();
    }

    [Fact]
    public void TheInstalledFilesAreEveryFileOfTheInstalledManifestsWithPathSizeAndHash()
    {
        var source = new FakeManifestSource();
        source.AddNamed(Depot, 200, files: [("Game/regulation.bin", Regulation)]);
        source.AddNamed(Depot + 1, 300, files: [("Game/other.dll", Other)]);

        var files = Vouching(source).InstalledFiles(Install(200, 300));

        files.Should().BeEquivalentTo(
        [
            new ManifestFile("Game/regulation.bin", (ulong)Regulation.Length, FakeManifestSource.Sha1(Regulation)),
            new ManifestFile("Game/other.dll", (ulong)Other.Length, FakeManifestSource.Sha1(Other)),
        ]);
    }

    [Fact]
    public void AnOlderCachedManifestIsNotPartOfTheInstalledFiles()
    {
        // The older build's files are not what the folder should hold now.
        var source = new FakeManifestCatalog();
        source.AddNamed(Depot, 200, files: [("Game/new.dll", Other)]);
        source.AddNamed(Depot, 100, files: [("Game/old.dll", Regulation)]);

        Vouching(source).InstalledFiles(Install(200))!.Select(f => f.Path).Should().Equal("Game/new.dll");
    }

    [Fact]
    public void EncryptedNamesStillListTheHashesWhenTheSourceCanReadThem()
    {
        var source = new FakeManifestCatalog();
        source.AddNamed(Depot, 200, encryptedNames: true, files: [("Game/regulation.bin", Regulation)]);

        var file = Vouching(source).InstalledFiles(Install(200)).Should().ContainSingle().Subject;

        file.Path.Should().BeNull();
        file.Hash.Should().Be(FakeManifestSource.Sha1(Regulation));
    }

    [Fact]
    public void EncryptedNamesAScanOnlySourceCannotReadGiveNoInstalledFiles()
    {
        var source = new FakeManifestSource();
        source.AddNamed(Depot, 200, encryptedNames: true, files: [("Game/regulation.bin", Regulation)]);

        Vouching(source).InstalledFiles(Install(200)).Should().BeNull();
    }

    [Fact]
    public void ASavedManifestListsTheInstalledFilesAfterSteamDroppedIt()
    {
        var source = new FakeManifestSource();
        source.AddNamed(Depot, 200, files: [("Game/regulation.bin", Regulation)]);
        var saved = new SavedManifestHashes(() => FileSystem.Shared.FromUnsanitizedFullPath(Path.Combine(_root, "saved")));
        var vouching = new SteamManifestVouching(source, NullLogger<SteamManifestVouching>.Instance, saved);
        vouching.InstalledFiles(Install(200)).Should().NotBeNull();

        source.Remove(Depot, 200);

        vouching.InstalledFiles(Install(200))!.Select(f => f.Path).Should().Equal("Game/regulation.bin");
    }

    [Fact]
    public void WithoutAReadableManifestThereAreNoInstalledFiles()
    {
        Vouching(new FakeManifestSource()).InstalledFiles(Install(200)).Should().BeNull();
        new SteamManifestVouching(null, NullLogger<SteamManifestVouching>.Instance).InstalledFiles(Install(200)).Should().BeNull();
    }
}
