using SilverModManager.Sdk.Hashes;
using SilverModManager.Games.TestFramework.Helpers;
using System.Collections.Immutable;
using System.Text;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Abstractions.Games.Merge;
using SilverModManager.Abstractions.Steam;
using SilverModManager.Abstractions.Steam.DTOs;
using SilverModManager.Abstractions.Steam.Values;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FileHashes.Tests;

/// <summary>
/// Whether a game folder's copy of a file may stand in for the vanilla ancestor of a merge, read from baseline rows
/// written into an in-memory local store.
/// </summary>
public sealed class BaselineVanillaTrustTests : IDisposable
{
    private const string GamePath = "/games/test";

    private readonly RealQueryEngineHost _host = new();
    private readonly IConnection _local;
    private readonly IFileHashesService _hashes = Substitute.For<IFileHashesService>();
    private readonly FakeDepotcache _depotcache = new();
    private readonly BaselineVanillaTrust _trust;

    public BaselineVanillaTrustTests()
    {
        _local = _host.OpenInMemory("local_hashes");
        _hashes.Local.Returns(_ => _local.Db);
        _trust = new BaselineVanillaTrust(_hashes, _depotcache);
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public void NoBaselineIsUntrusted()
    {
        IsTrusted("content/content0/scripts/x.ws", out var reason).Should().BeFalse();
        reason.Should().Be(VanillaTrustReasons.StoreFileListNotSavedYet);
    }

    [Fact]
    public async Task AFileListedNonVanillaIsUntrusted()
    {
        var baseline = await LocalBaselineRows.AddLocalBaseline(_local, GameStore.Steam, GamePath, LocalBaselineKind.DepotManifests, [("a.esm", "vanilla")]);
        await AddReport(baseline, "content/content0/scripts/x.ws", NonVanillaReason.HashDiffers);

        IsTrusted("content/content0/Scripts/X.ws", out var reason).Should().BeFalse();
        reason.Should().Be("listed as modified");
    }

    [Theory]
    [InlineData(NonVanillaReason.NotInManifest, "listed as not a file the store installed")]
    [InlineData(NonVanillaReason.SizeDiffers, "listed as a different size from the store's copy")]
    [InlineData(NonVanillaReason.HashDiffers, "listed as modified")]
    public async Task TheReasonNamesWhyTheFileIsListed(NonVanillaReason listed, string expected)
    {
        var baseline = await LocalBaselineRows.AddLocalBaseline(_local, GameStore.Steam, GamePath, LocalBaselineKind.DepotManifests, [("a.esm", "vanilla")]);
        await AddReport(baseline, "x.ws", listed);

        IsTrusted("x.ws", out var reason).Should().BeFalse();
        reason.Should().Be(expected);
    }

    [Fact]
    public async Task AListedFileWhoseHashMatchesIsTrusted()
    {
        await LocalBaselineRows.AddLocalBaseline(_local, GameStore.Steam, GamePath, LocalBaselineKind.DepotManifests, [("content/content0/scripts/x.ws", "vanilla")]);

        IsTrusted("content/content0/Scripts/X.ws", out var reason).Should().BeTrue();
        reason.Should().BeEmpty();
    }

    [Fact]
    public async Task ABaselineBuiltForOtherLocatorIdsTrustsNothing()
    {
        await LocalBaselineRows.AddLocalBaseline(_local, GameStore.Steam, GamePath, LocalBaselineKind.DepotManifests, [("a.esm", "vanilla")]);
        await HashRows.BuiltForLocatorIds(_local, GamePath, "1001");

        _trust.IsTrusted(Installation(locatorIds: ["1002"]), new GamePath(LocationId.Game, RelativePath.FromUnsanitizedInput("a.esm")), HashOf("vanilla"), out var reason).Should().BeFalse();
        reason.Should().Be(VanillaTrustReasons.StoreFileListNotSavedYet);
        _trust.IsTrusted(Installation(locatorIds: ["1001"]), new GamePath(LocationId.Game, RelativePath.FromUnsanitizedInput("a.esm")), HashOf("vanilla"), out _).Should().BeTrue();
    }

    [Fact]
    public async Task AListedFileEditedAfterTheBaselineWasBuiltIsUntrusted()
    {
        await LocalBaselineRows.AddLocalBaseline(_local, GameStore.Steam, GamePath, LocalBaselineKind.DepotManifests, [("x.ws", "vanilla")]);

        IsTrusted("x.ws", out var reason, content: "edited later").Should().BeFalse();
        reason.Should().Be("the game's copy differs from the checked baseline");
    }

    [Fact]
    public async Task AnAttestedFileWhoseSha1Matches_IsTrusted()
    {
        await HashRows.AddBaselineOwningManifest(_local, LocalBaselineKind.DepotManifests, 1002);
        await HashRows.AddAttestedFile(_local, 1002, "Data/x.esm", "vanilla");

        IsTrusted("data/X.esm", out var reason, locatorIds: ["1002"]).Should().BeTrue("the merge read the bytes, and they are the store's");
        reason.Should().BeEmpty();
    }

    [Fact]
    public async Task AnAttestedFileWhoseSha1Differs_IsUntrustedAsDifferingFromTheBaseline()
    {
        await HashRows.AddBaselineOwningManifest(_local, LocalBaselineKind.DepotManifests, 1002);
        await HashRows.AddAttestedFile(_local, 1002, "x.esm", "vanilla");

        IsTrusted("x.esm", out var reason, content: "edited later", locatorIds: ["1002"]).Should().BeFalse();
        reason.Should().Be("the game's copy differs from the checked baseline");
    }

    [Fact]
    public async Task AFileTheBaselineNeverListedIsUntrusted()
    {
        await LocalBaselineRows.AddLocalBaseline(_local, GameStore.Steam, GamePath, LocalBaselineKind.DepotManifests, [("other.ws", "vanilla")]);

        IsTrusted("x.ws", out var reason).Should().BeFalse();
        reason.Should().Be("the baseline never checked this file");
    }

    [Fact]
    public async Task TheBaselineIsFoundUnderTheFolderItWasWrittenFor()
    {
        // The baseline is keyed by the game registry's folder (Locations[Game]), which can differ from the locator's.
        await LocalBaselineRows.AddLocalBaseline(_local, GameStore.Steam, "/mnt/moved/test", LocalBaselineKind.DepotManifests, [("x.ws", "vanilla")]);

        IsTrusted("x.ws", out _).Should().BeFalse("only the locator's folder was searched");
        IsTrusted("x.ws", out var reason, gameFolder: FileSystem.Shared.FromUnsanitizedFullPath("/mnt/moved/test")).Should().BeTrue();
        reason.Should().BeEmpty();
    }

    [Fact]
    public async Task ABaselineOfAnotherInstallationDoesNotCount()
    {
        await LocalBaselineRows.AddLocalBaseline(_local, GameStore.Steam, "/games/other", LocalBaselineKind.DepotManifests, [], "x.ws");
        await LocalBaselineRows.AddLocalBaseline(_local, GameStore.GOG, GamePath, LocalBaselineKind.DepotManifests, [], "x.ws");

        IsTrusted("x.ws", out var reason).Should().BeFalse();
        reason.Should().Be(VanillaTrustReasons.StoreFileListNotSavedYet);
    }

    [Fact]
    public async Task APathOutsideTheGameFolderIsUntrusted()
    {
        await LocalBaselineRows.AddLocalBaseline(_local, GameStore.Steam, GamePath, LocalBaselineKind.DepotManifests, [("a.esm", "vanilla")]);
        var path = new GamePath(LocationId.From("WitcherDocuments"), RelativePath.FromUnsanitizedInput("user.settings"));

        _trust.IsTrusted(Installation(), path, HashOf("vanilla"), out var reason).Should().BeFalse();
        reason.Should().Be("outside the game folder");
    }

    [Fact]
    public void NoBaseline_TheStoresRowsForTheCurrentIdsListTheFileWithItsHash_IsTrusted()
    {
        StoreRows(("content/content0/scripts/x.ws", "vanilla"));

        IsTrusted("content/content0/Scripts/X.ws", out var reason, locatorIds: ["7"]).Should().BeTrue();
        reason.Should().BeEmpty();
    }

    [Fact]
    public void NoBaseline_TheStoresRowsListTheFileWithAnotherHash_IsUntrustedAsDifferingFromTheStoresList()
    {
        StoreRows(("x.ws", "vanilla"));

        IsTrusted("x.ws", out var reason, content: "edited", locatorIds: ["7"]).Should().BeFalse();
        reason.Should().Be("the game's copy differs from the store's file list for the installed build");
    }

    [Fact]
    public void NoBaseline_TheStoresRowsCoverTheBuildAndDoNotListTheFile_IsUntrustedAsNotInstalledByTheStore()
    {
        StoreRows(("other.ws", "vanilla"));
        _hashes.CoversInstalledBuild(default).ReturnsForAnyArgs(true);

        IsTrusted("x.ws", out var reason, locatorIds: ["7"]).Should().BeFalse();
        reason.Should().Be("not a file the store installed");
    }

    [Fact]
    public void NoBaselineNoRows_TheSavedDepotManifestListsTheFilesSha1_IsTrusted()
    {
        _depotcache.Add(7, ("content/content0/scripts/x.ws", "vanilla"));

        IsTrusted("content/content0/Scripts/X.ws", out var reason, locatorIds: ["7"]).Should().BeTrue();
        reason.Should().BeEmpty();
    }

    [Fact]
    public void NoBaselineNoRows_TheSavedDepotManifestListsAnotherSha1_IsUntrusted()
    {
        _depotcache.Add(7, ("x.ws", "vanilla"));

        IsTrusted("x.ws", out var reason, content: "edited", locatorIds: ["7"]).Should().BeFalse();
        reason.Should().Be("the game's copy differs from the store's file list for the installed build");
    }

    [Fact]
    public void NoBaselineNoRows_TheSavedManifestIsOnlyReadOnce()
    {
        _depotcache.Add(7, ("x.ws", "vanilla"));

        IsTrusted("x.ws", out _, locatorIds: ["7"]).Should().BeTrue();
        IsTrusted("x.ws", out _, locatorIds: ["7"]).Should().BeTrue();

        _depotcache.Reads.Should().Be(1);
    }

    [Fact]
    public void NoBaselineNoRows_NothingSavedForTheId_SaysSteamHasNotSavedTheFileListYet()
    {
        IsTrusted("x.ws", out var reason, locatorIds: ["7"]).Should().BeFalse();
        reason.Should().Be(VanillaTrustReasons.StoreFileListNotSavedYet);
    }

    [Fact]
    public void NoBaselineNoRows_TheSavedManifestNeedsADepotKey_IsTreatedAsNotSavedAndDoesNotThrow()
    {
        _depotcache.AddEncrypted(7);

        IsTrusted("x.ws", out var reason, locatorIds: ["7"]).Should().BeFalse();
        reason.Should().Be(VanillaTrustReasons.StoreFileListNotSavedYet);
    }

    [Fact]
    public void NoBaselineNoRows_OneOfTwoDepotsIsNotSaved_AFileTheSavedOneLacksIsNotSaidToBeForeign()
    {
        _depotcache.Add(7, ("other.ws", "vanilla"));

        IsTrusted("x.ws", out var reason, locatorIds: ["7", "8"]).Should().BeFalse();
        reason.Should().Be(VanillaTrustReasons.StoreFileListNotSavedYet);
    }

    private void StoreRows(params (string Path, string Content)[] files) =>
        _hashes.GetGameFiles(Arg.Any<GameInstallation>()).Returns(files.Select(f => new GameFileRecord
        {
            Path = new GamePath(LocationId.Game, RelativePath.FromUnsanitizedInput(f.Path)),
            Size = Size.FromLong(Encoding.UTF8.GetByteCount(f.Content)),
            Identity = FileIdentity.XxHash3(HashOf(f.Content).XxHash3),
        }).ToArray());

    private sealed class FakeDepotcache : IDepotManifestSource
    {
        private readonly Dictionary<ulong, LocalManifestResult> _results = new();
        private int _reads;

        public int Reads => _reads;

        public void Add(ulong id, params (string Path, string Content)[] files)
        {
            var manifestId = ManifestId.From(id);
            _results[id] = new LocalManifestResult.Parsed(new Manifest
            {
                ManifestId = manifestId,
                DepotId = DepotId.From((uint)id),
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
            });
        }

        public void AddEncrypted(ulong id) => _results[id] = new LocalManifestResult.NeedsDepotKey(DepotId.From((uint)id), ManifestId.From(id));

        public bool TryGetManifestPath(AbsolutePath steamRoot, ManifestId manifestId, AbsolutePath? installPath, out AbsolutePath manifestPath)
        {
            manifestPath = steamRoot.Combine($"depotcache/{manifestId.Value}.manifest");
            return _results.ContainsKey(manifestId.Value);
        }

        public LocalManifestResult Read(AbsolutePath manifestPath)
        {
            Interlocked.Increment(ref _reads);
            return _results[ulong.Parse(Path.GetFileNameWithoutExtension(manifestPath.FileName.ToString()))];
        }
    }

    private bool IsTrusted(string relative, out string reason, string content = "vanilla", AbsolutePath? gameFolder = null, string[]? locatorIds = null)
        => _trust.IsTrusted(Installation(gameFolder, locatorIds), new GamePath(LocationId.Game, RelativePath.FromUnsanitizedInput(relative)), HashOf(content), out reason);

    private static MultiHash HashOf(string content) => MultiHasher.HashStream(new MemoryStream(Encoding.UTF8.GetBytes(content))).GetAwaiter().GetResult();

    private static GameInstallation Installation(AbsolutePath? gameFolder = null, string[]? locatorIds = null)
    {
        var result = new GameLocatorResult
        {
            Game = Substitute.For<IGameData>(),
            Path = FileSystem.Shared.FromUnsanitizedFullPath(GamePath),
            Store = GameStore.Steam,
            StoreIdentifier = "1",
            LocatorIds = [..(locatorIds ?? []).Select(LocatorId.From)],
            Locator = Substitute.For<IGameLocator>(),
            StoreRoot = FileSystem.Shared.FromUnsanitizedFullPath("/steam"),
        };
        return new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, gameFolder ?? result.Path)));
    }

    private async Task AddReport(EntityId baseline, string path, NonVanillaReason reason)
    {
        using var tx = _local.BeginTransaction();
        _ = new NonVanillaFile.New(tx)
        {
            BaselineId = baseline,
            Path = RelativePath.FromUnsanitizedInput(path),
            Reason = reason,
            Size = Size.From(1),
        };
        await tx.Commit();
    }
}
