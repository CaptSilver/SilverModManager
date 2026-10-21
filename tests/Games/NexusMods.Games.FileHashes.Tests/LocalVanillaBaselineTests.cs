using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.Games.FileHashes;
using NexusMods.Abstractions.Games.FileHashes.Models;
using NexusMods.Abstractions.Steam.DTOs;
using NexusMods.Abstractions.Steam.Values;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.NexusModsApi;
using OperatingSystem = NexusMods.Abstractions.Games.FileHashes.Values.OperatingSystem;

namespace NexusMods.Games.FileHashes.Tests;

/// <summary>
/// Builds baselines from game directories and manifests made in temp directories; nothing here
/// reads a real store directory.
/// </summary>
public sealed class LocalVanillaBaselineTests : IDisposable
{
    private const string GamePathString = "/games/test";

    private readonly ServiceHarness _harness = new();
    private string _gameRoot;
    private readonly IConnection _store;
    private readonly List<string> _opened = [];
    private Func<AbsolutePath, Stream>? _openOverride;

    public LocalVanillaBaselineTests()
    {
        _gameRoot = Path.Combine(_harness.Root, "game");
        Directory.CreateDirectory(_gameRoot);
        _store = _harness.Host.OpenInMemory("local_hashes");
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task VanillaFile_IsWrittenWithItsHashes()
    {
        WriteFile("a.esm", "vanilla");

        var result = await Build([Manifest(1, ("a.esm", "vanilla"))]);

        var baseline = LocalBaseline.Load(_store.Db, result.BaselineId);
        baseline.Kind.Should().Be(LocalBaselineKind.DepotManifests);
        var file = baseline.Files.Should().ContainSingle().Subject;
        file.Path.ToString().Should().Be("a.esm");
        var expected = await Hash("vanilla");
        file.Hash.XxHash3.Should().Be(expected.XxHash3);
        file.Hash.Sha1.Should().Be(expected.Sha1);
        file.Hash.Md5.Should().Be(expected.Md5);
        file.Hash.Crc32.Should().Be(expected.Crc32);
        file.Hash.Size.Should().Be(expected.Size);
        result.NonVanilla.Should().BeEmpty();
    }

    [Fact]
    public async Task SizeMismatch_IsReportedWithoutHashing()
    {
        WriteFile("a.esm", "longer than the manifest says");

        var result = await Build([Manifest(1, ("a.esm", "short"))]);

        result.NonVanilla.Should().ContainSingle().Which.Reason.Should().Be(NonVanillaReason.SizeDiffers);
        _opened.Should().BeEmpty("a size difference decides the file without reading it");
        LocalBaseline.Load(_store.Db, result.BaselineId).Files.Should().BeEmpty();
    }

    [Fact]
    public async Task Sha1Mismatch_IsExcludedFromFilesAndReported()
    {
        WriteFile("a.esm", "modded!");

        var result = await Build([Manifest(1, ("a.esm", "vanilla"))]);

        result.NonVanilla.Should().ContainSingle().Which.Reason.Should().Be(NonVanillaReason.HashDiffers);
        LocalBaseline.Load(_store.Db, result.BaselineId).Files.Should().BeEmpty();
    }

    [Fact]
    public async Task ZeroSizeFileInTheManifest_IsVanilla()
    {
        WriteFile("empty.txt", "");

        var result = await Build([Manifest(1, ("empty.txt", ""))]);

        result.NonVanilla.Should().BeEmpty();
        LocalBaseline.Load(_store.Db, result.BaselineId).Files.Should().ContainSingle().Which.Path.ToString().Should().Be("empty.txt");
    }

    [Fact]
    public async Task MissingFile_IsSkippedSilently()
    {
        var result = await Build([Manifest(1, ("gone.esm", "vanilla"))]);

        result.NonVanilla.Should().BeEmpty();
        LocalBaseline.Load(_store.Db, result.BaselineId).Files.Should().BeEmpty();
    }

    [Fact]
    public async Task FileNotInAnyManifest_IsReportedWhenAllDepotsAreCovered()
    {
        WriteFile("mod.esp", "mod");

        var result = await Build([Manifest(1, ("a.esm", "vanilla"))], allCovered: true);

        result.NonVanilla.Should().ContainSingle().Which.Reason.Should().Be(NonVanillaReason.NotInManifest);
        _opened.Should().BeEmpty();
        LocalBaseline.Load(_store.Db, result.BaselineId).Files.Should().BeEmpty();
    }

    [Fact]
    public async Task FileNotInAnyManifest_IsTakenAsVanillaWhenCoverageIsPartial()
    {
        WriteFile("other-depot.bin", "from a depot without a manifest");

        var result = await Build([Manifest(1, ("a.esm", "vanilla"))], allCovered: false);

        result.NonVanilla.Should().BeEmpty();
        var baseline = LocalBaseline.Load(_store.Db, result.BaselineId);
        baseline.Kind.Should().Be(LocalBaselineKind.PartialDepotManifests);
        baseline.Files.Should().ContainSingle().Which.Path.ToString().Should().Be("other-depot.bin");
    }

    [Fact]
    public async Task FilteredPath_IsNeitherHashedNorReported()
    {
        WriteFile("a.esm", "vanilla");
        WriteFile("config.ini", "written by the game");

        var result = await Build([Manifest(1, ("a.esm", "vanilla"))], isIgnored: p => p.Path.ToString() == "config.ini");

        result.NonVanilla.Should().BeEmpty();
        _opened.Should().ContainSingle().Which.Should().EndWith("a.esm");
        LocalBaseline.Load(_store.Db, result.BaselineId).Files.Should().ContainSingle().Which.Path.ToString().Should().Be("a.esm");
    }

    [Fact]
    public async Task ManifestPathWithBackslashes_MatchesTheFileOnDisk()
    {
        WriteFile("Data/a.esm", "vanilla");
        var manifest = Manifest(1, ("Data\\a.esm", "vanilla"));

        var result = await Build([manifest]);

        result.NonVanilla.Should().BeEmpty();
        var file = LocalBaseline.Load(_store.Db, result.BaselineId).Files.Should().ContainSingle().Subject;
        file.Path.Should().Be(manifest.Files[0].Path);
    }

    [Fact]
    public async Task FileChangedWhileHashing_IsNeitherVanillaNorReported()
    {
        var path = WriteFile("a.esm", "vanilla");
        _openOverride = p =>
        {
            // The game or Steam rewrites the file between the first stat and the end of the read.
            File.AppendAllText(path, "!");
            return p.Read();
        };

        var result = await Build([Manifest(1, ("a.esm", "vanilla"))]);

        result.NonVanilla.Should().BeEmpty();
        LocalBaseline.Load(_store.Db, result.BaselineId).Files.Should().BeEmpty();
    }

    [Fact]
    public async Task FileDeletedBeforeItIsHashed_IsNeitherVanillaNorReported()
    {
        var path = WriteFile("a.esm", "vanilla");
        _openOverride = p =>
        {
            File.Delete(path);
            return p.Read();
        };

        var result = await Build([Manifest(1, ("a.esm", "vanilla"))]);

        result.NonVanilla.Should().BeEmpty();
        LocalBaseline.Load(_store.Db, result.BaselineId).Files.Should().BeEmpty();
    }

    [Fact]
    public async Task PartialBaseline_WritesRowsForTheReadableManifests()
    {
        WriteFile("a.esm", "vanilla");
        WriteFile("other-depot.bin", "from a depot without a manifest");
        var location = _harness.NewDatabaseLocation();
        var service = _harness.CreateService(location);

        var result = await Build([Manifest(7, ("a.esm", "vanilla"))], allCovered: false, versionName: "build-12345", connection: service.Local.Connection);

        LocalBaseline.Load(service.Local, result.BaselineId).Files.Should().HaveCount(2);
        var manifest = SteamManifest.All(service.Local).Should().ContainSingle().Subject;
        manifest.Files.Should().ContainSingle().Which.Path.ToString().Should().Be("a.esm");
        VersionDefinition.All(service.Local).Should().ContainSingle();
        service.TryGetVanityVersion(new LocatorIdsWithGameStore(GameStore.Steam, [LocatorId.From("7"), LocatorId.From("8")]), out var version).Should().BeTrue();
        version.Value.Should().Be("build-12345");
    }

    [Fact]
    public async Task NoManifests_ProducesASnapshotBaseline()
    {
        WriteFile("a.esm", "one");
        WriteFile("Data/b.esm", "two");

        var result = await Build([], versionName: null);

        var baseline = LocalBaseline.Load(_store.Db, result.BaselineId);
        baseline.Kind.Should().Be(LocalBaselineKind.Snapshot);
        baseline.VersionName.Should().BeEmpty();
        baseline.Files.Select(f => f.Path.ToString()).Should().BeEquivalentTo(["a.esm", "Data/b.esm"]);
        result.NonVanilla.Should().BeEmpty();
        SteamManifest.All(_store.Db).Should().BeEmpty();
        VersionDefinition.All(_store.Db).Should().BeEmpty();
    }

    [Fact]
    public async Task Rebuild_ReplacesThePreviousBaselineRows()
    {
        WriteFile("a.esm", "vanilla");
        WriteFile("mod.esp", "mod");
        await Build([Manifest(1, ("a.esm", "vanilla"))]);
        File.Delete(Path.Combine(_gameRoot, "mod.esp"));

        var second = await Build([Manifest(2, ("a.esm", "vanilla"))]);

        LocalBaseline.All(_store.Db).Should().ContainSingle().Which.Id.Should().Be(second.BaselineId);
        NonVanillaFile.All(_store.Db).Should().BeEmpty();
        SteamManifest.All(_store.Db).Should().ContainSingle().Which.ManifestId.Should().Be(ManifestId.From(2));
        VersionDefinition.All(_store.Db).Should().ContainSingle();
        PathHashRelation.All(_store.Db).Should().ContainSingle();
        HashRelation.All(_store.Db).Should().ContainSingle("hash rows are shared by content and kept");
    }

    [Fact]
    public async Task DepotManifestBaseline_WritesSteamManifestAndVersionDefinitionRows()
    {
        WriteFile("a.esm", "vanilla");
        var location = _harness.NewDatabaseLocation();
        var service = _harness.CreateService(location);

        await Build([Manifest(7, ("a.esm", "vanilla"))], versionName: "build-12345", connection: service.Local.Connection);

        var found = service.TryGetVanityVersion(new LocatorIdsWithGameStore(GameStore.Steam, [LocatorId.From("7")]), out var version);
        found.Should().BeTrue();
        version.Value.Should().Be("build-12345");
        var manifest = SteamManifest.All(service.Local).Should().ContainSingle().Subject;
        manifest.Name.Should().Be("public");
        manifest.Files.Should().ContainSingle();
    }

    [Fact]
    public async Task GameRootBehindASymlinkedAncestor_IsWalked()
    {
        var real = Path.Combine(_harness.Root, "real");
        Directory.CreateDirectory(Path.Combine(real, "lib", "game", "Data"));
        File.WriteAllText(Path.Combine(real, "lib", "game", "Data", "a.esm"), "vanilla");
        File.WriteAllText(Path.Combine(real, "lib", "game", "b.dll"), "mod");
        var alias = Path.Combine(_harness.Root, "alias");
        Directory.CreateSymbolicLink(alias, real);
        _gameRoot = Path.Combine(alias, "lib", "game");

        var result = await Build([Manifest(1, ("Data/a.esm", "vanilla"))]);

        LocalBaseline.Load(_store.Db, result.BaselineId).Files.Should().ContainSingle().Which.Path.ToString().Should().Be("Data/a.esm");
        result.NonVanilla.Should().ContainSingle().Which.Reason.Should().Be(NonVanillaReason.NotInManifest);
    }

    [Fact]
    public async Task GameRootThatIsItselfASymlink_IsWalked()
    {
        var real = Path.Combine(_harness.Root, "real-game");
        Directory.CreateDirectory(Path.Combine(real, "Data"));
        File.WriteAllText(Path.Combine(real, "Data", "a.esm"), "vanilla");
        var link = Path.Combine(_harness.Root, "linked-game");
        Directory.CreateSymbolicLink(link, real);
        _gameRoot = link;

        var result = await Build([Manifest(1, ("Data/a.esm", "vanilla"))]);

        LocalBaseline.Load(_store.Db, result.BaselineId).Files.Should().ContainSingle().Which.Path.ToString().Should().Be("Data/a.esm");
    }

    [Fact]
    public async Task SymlinkEntryInsideTheGameFolder_IsStillSkipped()
    {
        WriteFile("a.esm", "vanilla");
        var outside = Path.Combine(_harness.Root, "outside.txt");
        File.WriteAllText(outside, "elsewhere");
        File.CreateSymbolicLink(Path.Combine(_gameRoot, "link.txt"), outside);

        var result = await Build([Manifest(1, ("a.esm", "vanilla"))]);

        result.NonVanilla.Should().BeEmpty();
        LocalBaseline.Load(_store.Db, result.BaselineId).Files.Should().ContainSingle();
    }

    [Fact]
    public async Task EveryFileIgnored_FailsInsteadOfRecordingAnEmptyBaseline()
    {
        WriteFile("a.esm", "vanilla");
        WriteFile("Data/b.esm", "vanilla");

        var act = () => Build([Manifest(1, ("a.esm", "vanilla"))], isIgnored: _ => true);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain(_gameRoot);
        LocalBaseline.All(_store.Db).Should().BeEmpty();
    }

    [Fact]
    public async Task FolderWithOnlySymlinks_FailsInsteadOfRecordingAnEmptyBaseline()
    {
        var outside = Path.Combine(_harness.Root, "outside.txt");
        File.WriteAllText(outside, "elsewhere");
        File.CreateSymbolicLink(Path.Combine(_gameRoot, "link.txt"), outside);

        var act = () => Build([Manifest(1, ("a.esm", "vanilla"))]);

        await act.Should().ThrowAsync<InvalidOperationException>();
        LocalBaseline.All(_store.Db).Should().BeEmpty();
    }

    [Fact]
    public async Task ClassifyingFarFewerFilesThanTheFolderHolds_Fails()
    {
        for (var i = 0; i < 20; i++) WriteFile($"ignored/{i}.bin", "x");
        WriteFile("a.esm", "vanilla");

        var act = () => Build([Manifest(1, ("a.esm", "vanilla"))], isIgnored: p => p.Path.ToString().StartsWith("ignored", StringComparison.Ordinal));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task FailedBuild_KeepsThePreviousBaseline()
    {
        WriteFile("a.esm", "vanilla");
        var first = await Build([Manifest(1, ("a.esm", "vanilla"))]);

        var act = () => Build([Manifest(1, ("a.esm", "vanilla"))], isIgnored: _ => true);

        await act.Should().ThrowAsync<InvalidOperationException>();
        LocalBaseline.All(_store.Db).Should().ContainSingle().Which.Id.Should().Be(first.BaselineId);
    }

    [Fact]
    public async Task EmptyGameFolder_IsStillABaseline()
    {
        var result = await Build([Manifest(1, ("a.esm", "vanilla"))]);

        LocalBaseline.Load(_store.Db, result.BaselineId).Files.Should().BeEmpty();
    }

    private Task<BaselineBuild> Build(Manifest[] manifests, bool allCovered = true, string? versionName = "1000",
        Func<GamePath, bool>? isIgnored = null, IConnection? connection = null)
    {
        var baseline = new LocalVanillaBaseline(FileSystem.Shared, NullLogger<LocalVanillaBaseline>.Instance, p =>
        {
            _opened.Add(p.ToString());
            return (_openOverride ?? (x => x.Read()))(p);
        });
        return baseline.Build(new BaselineRequest
        {
            GameRoot = FileSystem.Shared.FromUnsanitizedFullPath(_gameRoot),
            Store = GameStore.Steam,
            AppId = AppId.From(1),
            GamePath = GamePathString,
            LocatorIds = manifests.Select(m => m.ManifestId.Value.ToString()).ToArray(),
            VersionName = versionName,
            Manifests = manifests,
            AllInstalledDepotsCovered = allCovered,
            IsIgnored = isIgnored ?? (_ => false),
            GameId = HashRows.GameId,
            OperatingSystem = OperatingSystem.Windows,
        }, connection ?? _store);
    }

    private string WriteFile(string relative, string content)
    {
        var path = Path.Combine(_gameRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static Manifest Manifest(ulong id, params (string Path, string Content)[] files) => new()
    {
        ManifestId = ManifestId.From(id),
        DepotId = DepotId.From(2),
        CreationTime = DateTimeOffset.UnixEpoch,
        TotalCompressedSize = Size.Zero,
        TotalUncompressedSize = Size.Zero,
        Files = files.Select(f => new Manifest.FileData
        {
            Path = RelativePath.FromUnsanitizedInput(f.Path),
            Size = Size.FromLong(Encoding.UTF8.GetByteCount(f.Content)),
            Hash = Sha1Value.From(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(f.Content))),
            Chunks = [],
        }).ToArray(),
    };

    private static Task<MultiHash> Hash(string content) => MultiHasher.HashStream(new MemoryStream(Encoding.UTF8.GetBytes(content)));
}
