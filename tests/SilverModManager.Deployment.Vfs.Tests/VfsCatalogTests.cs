using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Deployment.Vfs.Tests;

/// <summary>
/// The fingerprint-to-hash cache over the real in-memory database: a hit needs every field of the
/// fingerprint to match, and a path never has more than one row.
/// </summary>
public class VfsCatalogTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<VfsCatalogTests>(helper), IDisposable
{
    private readonly AbsolutePath _directory = NexusMods.Paths.FileSystem.Shared
        .GetKnownPath(KnownPath.TempDirectory)
        .Combine($"vfs-catalog-tests-{Guid.NewGuid():N}");

    private static readonly AbsolutePath SomeFile = NexusMods.Paths.FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory).Combine("catalog-subject.bin");
    private static readonly DateTimeOffset Stamp = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero).AddTicks(1234567);
    private static readonly FileFingerprint Fingerprint = new(Device: 64768, Inode: 4242, Size: Size.From(10), MTime: Stamp, CTime: Stamp.AddSeconds(1));
    private static readonly Hash TheHash = Hash.From(0xDEADBEEF);

    private async Task<GameInstallMetadataId> Installation()
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        var loadout = await CreateLoadout();
        return Loadout.Load(Connection.Db, loadout.LoadoutId).InstallationId;
    }

    private async Task Record(IVfsCatalog catalog, GameInstallMetadataId installation, AbsolutePath path, FileFingerprint fingerprint, Hash hash)
    {
        using var tx = Connection.BeginTransaction();
        catalog.Record(tx, installation, path, fingerprint, hash);
        await tx.Commit();
    }

    [Fact]
    public async Task AnEmptyCatalogMisses()
    {
        var installation = await Installation();
        var catalog = ServiceProvider.GetRequiredService<IVfsCatalog>();

        catalog.TryGetHash(installation, SomeFile, Fingerprint, out _).Should().BeFalse();
    }

    [Fact]
    public async Task ARecordedFingerprintReturnsItsHash()
    {
        var installation = await Installation();
        var catalog = ServiceProvider.GetRequiredService<IVfsCatalog>();
        await Record(catalog, installation, SomeFile, Fingerprint, TheHash);

        catalog.TryGetHash(installation, SomeFile, Fingerprint, out var hash).Should().BeTrue();
        hash.Should().Be(TheHash);
    }

    public static TheoryData<string, FileFingerprint> OneFieldDiffers => new()
    {
        { "mtime", Fingerprint with { MTime = Stamp.AddTicks(1) } },
        { "ctime", Fingerprint with { CTime = Stamp.AddTicks(1) } },
        { "inode", Fingerprint with { Inode = Fingerprint.Inode + 1 } },
        { "device", Fingerprint with { Device = Fingerprint.Device + 1 } },
        { "size", Fingerprint with { Size = Size.From(11) } },
    };

    [Theory]
    [MemberData(nameof(OneFieldDiffers))]
    public async Task AFingerprintDifferingInOneFieldMisses(string field, FileFingerprint changed)
    {
        var installation = await Installation();
        var catalog = ServiceProvider.GetRequiredService<IVfsCatalog>();
        await Record(catalog, installation, SomeFile, Fingerprint, TheHash);

        catalog.TryGetHash(installation, SomeFile, changed, out _).Should().BeFalse($"{field} differs");
    }

    [Fact]
    public async Task APathIsKeyedByItsInstallation()
    {
        var installation = await Installation();
        var catalog = ServiceProvider.GetRequiredService<IVfsCatalog>();
        await Record(catalog, installation, SomeFile, Fingerprint, TheHash);

        catalog.TryGetHash(GameInstallMetadataId.From(EntityId.From(installation.Value.Value + 1)), SomeFile, Fingerprint, out _).Should().BeFalse();
        catalog.TryGetHash(installation, SomeFile.Parent.Combine("other.bin"), Fingerprint, out _).Should().BeFalse();
    }

    [Fact]
    public async Task RecordingAPathAgainReplacesItsRow()
    {
        var installation = await Installation();
        var catalog = ServiceProvider.GetRequiredService<IVfsCatalog>();
        var newer = Fingerprint with { MTime = Stamp.AddMinutes(1) };
        await Record(catalog, installation, SomeFile, Fingerprint, TheHash);
        await Record(catalog, installation, SomeFile, newer, Hash.From(7));

        VfsCatalogEntry.FindByInstallation(Connection.Db, installation).Should().ContainSingle();
        catalog.TryGetHash(installation, SomeFile, Fingerprint, out _).Should().BeFalse();
        catalog.TryGetHash(installation, SomeFile, newer, out var hash).Should().BeTrue();
        hash.Should().Be(Hash.From(7));
    }

    private sealed class CountingHasher : IFileHasher
    {
        public int Calls;

        public async ValueTask<Hash> Hash(GameInstallMetadataId installation, AbsolutePath file, CancellationToken token = default)
        {
            Calls++;
            return await new XxHash3FileHasher().Hash(installation, file, token);
        }
    }

    [Fact]
    public async Task TheHasherReadsAFileOnceAndThenAnswersFromTheCatalog()
    {
        var installation = await Installation();
        var inner = new CountingHasher();
        var hasher = new CatalogBackedHasher(ServiceProvider.GetRequiredService<IVfsCatalog>(), ServiceProvider.GetRequiredService<IBackingScanner>(), Connection, inner);
        var file = _directory.Combine("a.bin");
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync("some bytes");

        var first = await hasher.Hash(installation, file);
        var second = await hasher.Hash(installation, file);

        second.Should().Be(first);
        inner.Calls.Should().Be(1);
        VfsCatalogEntry.FindByInstallation(Connection.Db, installation).Should().ContainSingle();
    }

    [Fact]
    public async Task TouchingAFileHashesItAgainAndKeepsOneRow()
    {
        var installation = await Installation();
        var inner = new CountingHasher();
        var hasher = new CatalogBackedHasher(ServiceProvider.GetRequiredService<IVfsCatalog>(), ServiceProvider.GetRequiredService<IBackingScanner>(), Connection, inner);
        var file = _directory.Combine("b.bin");
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync("some bytes");
        await hasher.Hash(installation, file);

        File.SetLastWriteTimeUtc(file.ToString(), File.GetLastWriteTimeUtc(file.ToString()).AddMinutes(5));
        await hasher.Hash(installation, file);

        inner.Calls.Should().Be(2);
        VfsCatalogEntry.FindByInstallation(Connection.Db, installation).Should().ContainSingle();
    }

    [Fact]
    public async Task AFileTheHasherCannotStatFailsLoudly()
    {
        var installation = await Installation();
        var hasher = new CatalogBackedHasher(ServiceProvider.GetRequiredService<IVfsCatalog>(), ServiceProvider.GetRequiredService<IBackingScanner>(), Connection, new CountingHasher());

        var act = async () => await hasher.Hash(installation, _directory.Combine("missing.bin"));

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task TheFactoryHandsPromotersTheCatalogBackedHasher()
    {
        var installation = await Installation();

        var promoter = ServiceProvider.GetRequiredService<StagingPromoterFactory>().For(installation);

        promoter.Hasher.Should().BeOfType<CatalogBackedHasher>();
    }

    [Fact]
    public void TheGraphHoldsNoStagingPromoter()
    {
        ServiceProvider.GetService<StagingPromoter>().Should().BeNull("a promoter belongs to one installation, so the factory is what the container holds");
    }

    public void Dispose()
    {
        if (_directory.DirectoryExists()) _directory.DeleteDirectory(recursive: true);
    }
}
