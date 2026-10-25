using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Steam;
using SilverModManager.Abstractions.Steam.Values;
using NexusMods.Paths;
using SilverModManager.Sdk.Hashes;
using Xunit;

namespace SilverModManager.Networking.Steam.Tests;

public sealed class DepotCacheTests : IDisposable
{
    private static readonly DepotId Depot = DepotId.From(489831);

    private readonly AbsolutePath _dir = FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory) / $"depot-cache-{Guid.NewGuid():N}";
    private readonly DepotCache _cache = new(NullLogger.Instance);

    public DepotCacheTests() => _dir.CreateDirectory();
    public void Dispose() => _dir.DeleteDirectory(true);

    private AbsolutePath Root => _dir / "steam";

    private static string ManifestFileName(DepotId depot, ManifestId id) => $"{depot.Value}_{id.Value}.manifest";

    private static AbsolutePath Touch(AbsolutePath path)
    {
        path.Parent.CreateDirectory();
        File.WriteAllBytes(path.ToString(), []);
        return path;
    }

    [Fact]
    public void TryGetManifestPath_ResolvesByManifestId()
    {
        var older = Touch(Root / "depotcache" / ManifestFileName(Depot, ManifestId.From(111)));
        var current = Touch(Root / "depotcache" / ManifestFileName(Depot, ManifestId.From(222)));

        _cache.TryGetManifestPath(Root, ManifestId.From(222), null, out var found).Should().BeTrue();
        found.Should().Be(current);

        _cache.TryGetManifestPath(Root, ManifestId.From(111), null, out found).Should().BeTrue();
        found.Should().Be(older);
    }

    [Fact]
    public void TryGetManifestPath_DoesNotMatchAManifestIdThatOnlyEndsTheSame()
    {
        Touch(Root / "depotcache" / ManifestFileName(Depot, ManifestId.From(1222)));

        _cache.TryGetManifestPath(Root, ManifestId.From(222), null, out _).Should().BeFalse();
    }

    [Fact]
    public void TryGetManifestPath_UnknownId_ReturnsFalse()
    {
        Touch(Root / "depotcache" / ManifestFileName(Depot, ManifestId.From(111)));

        _cache.TryGetManifestPath(Root, ManifestId.From(999), null, out _).Should().BeFalse();
    }

    [Fact]
    public void TryGetManifestPath_NoDepotcacheDirectory_ReturnsFalse()
    {
        Root.CreateDirectory();

        _cache.TryGetManifestPath(Root, ManifestId.From(1), Root / "steamapps" / "common" / "Game", out _).Should().BeFalse();
    }

    [Fact]
    public void TryGetManifestPath_FallsBackToTheInstallsLibraryDepotcache()
    {
        Root.CreateDirectory();
        var library = _dir / "library";
        var expected = Touch(library / "steamapps" / "depotcache" / ManifestFileName(Depot, ManifestId.From(5)));

        _cache.TryGetManifestPath(Root, ManifestId.From(5), library / "steamapps" / "common" / "Game", out var found).Should().BeTrue();
        found.Should().Be(expected);
    }

    [Fact]
    public void TryGetManifestPath_PrefersTheClientRootOverTheLibrary()
    {
        var inRoot = Touch(Root / "depotcache" / ManifestFileName(Depot, ManifestId.From(5)));
        var library = _dir / "library";
        Touch(library / "steamapps" / "depotcache" / ManifestFileName(Depot, ManifestId.From(5)));

        _cache.TryGetManifestPath(Root, ManifestId.From(5), library / "steamapps" / "common" / "Game", out var found).Should().BeTrue();
        found.Should().Be(inRoot);
    }

    [Fact]
    public void TryGetManifestPath_ShallowInstallPath_DoesNotThrow()
    {
        Root.CreateDirectory();
        var act = () => _cache.TryGetManifestPath(Root, ManifestId.From(1), FileSystem.Shared.FromUnsanitizedFullPath("/"), out _);

        act.Should().NotThrow();
    }

    [Fact]
    public void TryGetManifestPath_SameManifestIdInTwoDepots_TakesTheFirstByOrdinalOrder()
    {
        Touch(Root / "depotcache" / ManifestFileName(DepotId.From(20), ManifestId.From(7)));
        var first = Touch(Root / "depotcache" / ManifestFileName(DepotId.From(10), ManifestId.From(7)));

        _cache.TryGetManifestPath(Root, ManifestId.From(7), null, out var found).Should().BeTrue();
        found.Should().Be(first);
    }

    [Fact]
    public void LocalDepotManifests_ResolvesAndReadsASynthesizedManifest()
    {
        var id = ManifestId.From(321);
        DepotManifestFixture.Write(Root / "depotcache" / ManifestFileName(Depot, id), Depot, id, [("a.bin", [1, 2, 3])]);
        var source = new LocalDepotManifests(_cache, new LocalManifestReader());

        source.TryGetManifestPath(Root, id, null, out var path).Should().BeTrue();
        var result = source.Read(path).Should().BeOfType<LocalManifestResult.Parsed>().Subject;

        result.Manifest.ManifestId.Should().Be(id);
        result.Manifest.Files.Should().ContainSingle(f => f.Path == "a.bin");
    }

    [Fact]
    public void AddSteam_RegistersTheManifestSourceOnce()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSteam();
        services.AddSteam();

        using var provider = services.BuildServiceProvider();

        provider.GetServices<IDepotManifestSource>().Should().ContainSingle().Which.Should().BeOfType<LocalDepotManifests>();
    }

    [Fact]
    public void CachedManifests_ListsEveryManifestOfTheDepotAndNoOtherDepot()
    {
        var older = Touch(Root / "depotcache" / ManifestFileName(Depot, ManifestId.From(111)));
        var newer = Touch(Root / "depotcache" / ManifestFileName(Depot, ManifestId.From(222)));
        Touch(Root / "depotcache" / ManifestFileName(DepotId.From(5), ManifestId.From(333)));
        Touch(Root / "depotcache" / "readme.txt");

        _cache.CachedManifests(Root, null, Depot).Should().Equal(older, newer);
    }

    [Fact]
    public void CachedManifests_DoesNotMatchADepotThatOnlyStartsTheSame()
    {
        Touch(Root / "depotcache" / ManifestFileName(DepotId.From(4898311), ManifestId.From(1)));

        _cache.CachedManifests(Root, null, Depot).Should().BeEmpty();
    }

    [Fact]
    public void CachedManifests_AddsTheInstallsLibraryDepotcacheAfterTheClientRoot()
    {
        var inRoot = Touch(Root / "depotcache" / ManifestFileName(Depot, ManifestId.From(1)));
        var library = _dir / "library";
        var inLibrary = Touch(library / "steamapps" / "depotcache" / ManifestFileName(Depot, ManifestId.From(2)));

        _cache.CachedManifests(Root, library / "steamapps" / "common" / "Game", Depot).Should().Equal(inRoot, inLibrary);
    }

    [Fact]
    public void CachedManifests_NoDepotcacheIsEmpty()
    {
        Root.CreateDirectory();

        _cache.CachedManifests(Root, null, Depot).Should().BeEmpty();
    }

    [Fact]
    public void LocalDepotManifests_ListsAndReadsHashesOfAnOlderManifestOfTheDepot()
    {
        var older = ManifestId.From(10);
        byte[] content = [9, 9, 9];
        DepotManifestFixture.Write(Root / "depotcache" / ManifestFileName(Depot, older), Depot, older, [("a.bin", content)], encryptFilenames: true);
        var source = new LocalDepotManifests(_cache, new LocalManifestReader());

        var listed = source.CachedManifests(Root, null, Depot).Should().ContainSingle().Subject;
        var hashes = source.ReadFileHashes(listed);

        hashes!.ManifestId.Should().Be(older);
        hashes.Hashes.Should().ContainSingle().Which.Should().Be(Sha1Value.From(System.Security.Cryptography.SHA1.HashData(content)));
    }

    [Fact]
    public void AddSteam_RegistersTheManifestSourceAsACatalogToo()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSteam();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IDepotManifestSource>().Should().BeAssignableTo<IDepotManifestCatalog>();
    }
}
