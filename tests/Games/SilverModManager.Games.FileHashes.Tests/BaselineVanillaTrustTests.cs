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
    private readonly BaselineVanillaTrust _trust;

    public BaselineVanillaTrustTests()
    {
        _local = _host.OpenInMemory("local_hashes");
        var hashes = Substitute.For<IFileHashesService>();
        hashes.Local.Returns(_ => _local.Db);
        _trust = new BaselineVanillaTrust(hashes);
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public void NoBaselineIsUntrusted()
    {
        IsTrusted("content/content0/scripts/x.ws", out var reason).Should().BeFalse();
        reason.Should().Contain("not been checked");
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
        reason.Should().Contain("not been checked");
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
        reason.Should().Contain("not been checked");
    }

    [Fact]
    public async Task APathOutsideTheGameFolderIsUntrusted()
    {
        await LocalBaselineRows.AddLocalBaseline(_local, GameStore.Steam, GamePath, LocalBaselineKind.DepotManifests, [("a.esm", "vanilla")]);
        var path = new GamePath(LocationId.From("WitcherDocuments"), RelativePath.FromUnsanitizedInput("user.settings"));

        _trust.IsTrusted(Installation(), path, HashOf("vanilla"), out var reason).Should().BeFalse();
        reason.Should().Be("outside the game folder");
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
