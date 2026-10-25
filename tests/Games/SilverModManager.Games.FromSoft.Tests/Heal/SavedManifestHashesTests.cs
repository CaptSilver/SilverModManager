using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Steam.Values;
using SilverModManager.Games.FromSoft.Heal;
using SilverModManager.Games.FromSoft.Tests.Saves;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.FromSoft.Tests.Heal;

public sealed class SavedManifestHashesTests : IDisposable
{
    private static readonly byte[] Regulation = "the shipped regulation"u8.ToArray();
    private static readonly byte[] Other = "something else"u8.ToArray();
    private const uint Depot = 1245621;
    private const string AppId = "1245620";

    private readonly string _root = Directory.CreateTempSubdirectory("saved-manifests").FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private AbsolutePath SteamRoot => FileSystem.Shared.FromUnsanitizedFullPath(Path.Combine(_root, "steam"));

    private GameInstallation Install(params ulong[] manifestIds) => HealFixtures.Game(Path.Combine(_root, "game"), storeRoot: SteamRoot, manifestIds: manifestIds);

    private SavedManifestHashes Archive(ILogger? logger = null) =>
        new(() => FileSystem.Shared.FromUnsanitizedFullPath(Path.Combine(_root, "saved")), _time, logger);

    private sealed class CollectingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public void TheWholeFileListIsSavedWithTheBuildItWasSeenFor()
    {
        var source = new FakeManifestCatalog();
        source.Add(Depot, 200, files: [Regulation, Other]);
        var archive = Archive();

        archive.Capture(source, Install(200)).Should().Be(1);

        var saved = archive.Saved().Should().ContainSingle().Subject;
        saved.AppId.Should().Be(AppId);
        saved.BuildId.Should().Be("23850278");
        saved.DepotId.Should().Be(DepotId.From(Depot));
        saved.ManifestId.Should().Be(ManifestId.From(200));
        saved.FirstSeen.Should().Be(_time.GetUtcNow());
        archive.Files(ManifestId.From(200)).Select(f => (f.Path, f.Size, f.Hash))
            .Should().Equal(("file0.bin", (ulong)Regulation.Length, FakeManifestSource.Sha1(Regulation)),
                ("file1.bin", (ulong)Other.Length, FakeManifestSource.Sha1(Other)));
    }

    [Fact]
    public void ASecondCaptureSavesNothingAndKeepsWhenTheManifestWasFirstSeen()
    {
        var source = new FakeManifestCatalog();
        source.Add(Depot, 200, files: [Regulation]);
        var archive = Archive();
        archive.Capture(source, Install(200));
        var firstSeen = _time.GetUtcNow();
        _time.Advance(TimeSpan.FromDays(3));
        source.Reads.Clear();

        archive.Capture(source, Install(200)).Should().Be(0);

        archive.Saved().Should().ContainSingle().Which.FirstSeen.Should().Be(firstSeen);
        source.Reads.Should().BeEmpty("a manifest that is already saved is not read again");
    }

    [Fact]
    public void ManifestsSteamKeptForOlderBuildsOfTheSameDepotAreSavedToo()
    {
        var source = new FakeManifestCatalog();
        source.Add(Depot, 200, files: [Other]);
        source.Add(Depot, 100, files: [Regulation]);
        source.Add(99, 300, files: [Other]);
        var archive = Archive();

        archive.Capture(source, Install(200)).Should().Be(2);

        archive.Saved().Select(s => s.ManifestId.Value).Should().BeEquivalentTo([100UL, 200UL]);
    }

    [Fact]
    public void OnlyTheInstallsOwnManifestIsSavedWithTheInstalledBuildId()
    {
        var source = new FakeManifestCatalog();
        source.Add(Depot, 200, files: [Other]);
        source.Add(Depot, 100, files: [Regulation]);
        var archive = Archive();

        archive.Capture(source, Install(200));

        var saved = archive.Saved().ToDictionary(s => s.ManifestId.Value, s => s.BuildId);
        saved[200].Should().Be("23850278");
        saved[100].Should().BeNull("the build an older cached manifest shipped with is not known");
    }

    [Fact]
    public void ManifestsWithEncryptedNamesAreSavedWithoutPaths()
    {
        var source = new FakeManifestCatalog();
        source.Add(Depot, 200, encryptedNames: true, files: [Regulation]);
        var archive = Archive();

        archive.Capture(source, Install(200));

        archive.Files(ManifestId.From(200)).Should().ContainSingle().Which.Path.Should().BeNull();
    }

    [Fact]
    public void ASourceThatOnlyReadsNamedManifestsSavesThoseAndSkipsTheRest()
    {
        var source = new FakeManifestSource();
        source.Add(Depot, 200, files: [Regulation]);
        source.Add(Depot, 201, encryptedNames: true, files: [Other]);
        var archive = Archive();

        archive.Capture(source, Install(200, 201)).Should().Be(1);

        archive.Saved().Should().ContainSingle().Which.ManifestId.Should().Be(ManifestId.From(200));
    }

    [Fact]
    public void AGarbageManifestIsSkippedAndLoggedWhileTheRestAreSaved()
    {
        var source = new FakeManifestCatalog();
        source.Add(Depot, 200, files: [Regulation]);
        source.AddGarbage(Depot, 150);
        var logger = new CollectingLogger();
        var archive = Archive(logger);

        archive.Capture(source, Install(200)).Should().Be(1);

        archive.Saved().Should().ContainSingle().Which.ManifestId.Should().Be(ManifestId.From(200));
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("150"));
    }

    [Fact]
    public void ASavedHashStillVouchesAfterSteamDeletesTheManifest()
    {
        var source = new FakeManifestCatalog();
        source.Add(Depot, 200, files: [Regulation, Other]);
        var archive = Archive();
        archive.Capture(source, Install(200));
        source.Remove(Depot, 200);

        var vouching = new SteamManifestVouching(source, NullLogger<SteamManifestVouching>.Instance, archive);

        vouching.Find(Install(200), FakeManifestSource.Sha1(Regulation))
            .Should().Be(new ManifestMatch(ManifestId.From(200), DepotId.From(Depot)));
    }

    [Fact]
    public void AHashNoSavedManifestCarriesDoesNotVouch()
    {
        var source = new FakeManifestCatalog();
        source.Add(Depot, 200, files: [Other]);
        var archive = Archive();
        archive.Capture(source, Install(200));
        source.Remove(Depot, 200);

        new SteamManifestVouching(source, NullLogger<SteamManifestVouching>.Instance, archive)
            .Find(Install(200), FakeManifestSource.Sha1(Regulation)).Should().BeNull();
    }

    [Fact]
    public void AHashSavedForAnotherGameDoesNotVouch()
    {
        var source = new FakeManifestCatalog();
        source.Add(Depot, 200, files: [Regulation]);
        var archive = Archive();
        archive.Capture(source, Install(200));

        archive.Find("999999", FakeManifestSource.Sha1(Regulation)).Should().BeNull();
        archive.Find(AppId, FakeManifestSource.Sha1(Regulation)).Should().NotBeNull();
    }

    [Fact]
    public void VouchingSavesTheManifestsItReads()
    {
        var source = new FakeManifestCatalog();
        source.Add(Depot, 200, files: [Regulation]);
        var archive = Archive();

        new SteamManifestVouching(source, NullLogger<SteamManifestVouching>.Instance, archive)
            .Find(Install(200), FakeManifestSource.Sha1(Regulation));

        archive.Saved().Should().ContainSingle().Which.ManifestId.Should().Be(ManifestId.From(200));
    }

    [Fact]
    public void SavedHashesVouchWithoutAnyManifestSource()
    {
        var source = new FakeManifestCatalog();
        source.Add(Depot, 200, files: [Regulation]);
        Archive().Capture(source, Install(200));

        // A fresh archive over the same folder, as after a restart with Steam's cache gone.
        new SteamManifestVouching(null, NullLogger<SteamManifestVouching>.Instance, Archive())
            .Find(Install(200), FakeManifestSource.Sha1(Regulation))!.ManifestId.Should().Be(ManifestId.From(200));
    }

    [Fact]
    public void ALiveManifestIsPreferredToASavedOne()
    {
        var source = new FakeManifestCatalog();
        source.Add(Depot, 100, files: [Regulation]);
        var archive = Archive();
        archive.Capture(source, Install(100));
        source.Add(Depot, 200, files: [Regulation]);

        new SteamManifestVouching(source, NullLogger<SteamManifestVouching>.Instance, archive)
            .Find(Install(200), FakeManifestSource.Sha1(Regulation))!.ManifestId.Should().Be(ManifestId.From(200));
    }

    [Fact]
    public void ACorruptSavedFileIsSkippedAndLogged()
    {
        var source = new FakeManifestCatalog();
        source.Add(Depot, 200, files: [Regulation]);
        Archive().Capture(source, Install(200));
        File.WriteAllText(Directory.EnumerateFiles(Path.Combine(_root, "saved")).Single(), "{ not json");
        var logger = new CollectingLogger();

        Archive(logger).Find(AppId, FakeManifestSource.Sha1(Regulation)).Should().BeNull();

        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void TheStartupPassSavesTheManifestsOfEveryLocatedGame()
    {
        var source = new FakeManifestCatalog();
        source.Add(Depot, 200, files: [Regulation]);
        var registry = Substitute.For<IGameRegistry>();
        var installation = Install(200);
        registry.LocateGameInstallations().Returns([installation]);
        var archive = Archive();

        new SavedManifestsStartupPass(registry, source, archive, NullLogger<SavedManifestsStartupPass>.Instance).Run(CancellationToken.None);

        archive.Saved().Should().ContainSingle().Which.ManifestId.Should().Be(ManifestId.From(200));
    }
}
