using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Games.FileHashes.Tests;

/// <summary>
/// A <see cref="HashRelation"/> row read as <see cref="FileIdentity"/> values, one per digest it records.
/// </summary>
public sealed class HashRelationIdentityTests : IDisposable
{
    private readonly ServiceHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task IdentityOfARecordedKind_IsTheFactoryIdentityOfThatDigest()
    {
        var hashes = await MultiHasher.HashStream(new MemoryStream("vanilla"u8.ToArray()));
        var row = await Commit(hashes, withMd5: false);

        row.Identity(HashKind.XxHash3).Should().Be(FileIdentity.XxHash3(hashes.XxHash3));
        row.Identity(HashKind.Sha1).Should().Be(FileIdentity.Sha1(hashes.Sha1));
        row.Identity(HashKind.Crc32).Should().Be(FileIdentity.Crc32(hashes.Crc32));
        row.Identity(HashKind.XxHash64).Should().Be(FileIdentity.XxHash64(hashes.XxHash64));
    }

    [Fact]
    public async Task IdentityOfAnAttributeTheRowLacks_Throws()
    {
        var hashes = await MultiHasher.HashStream(new MemoryStream("vanilla"u8.ToArray()));
        var row = await Commit(hashes, withMd5: false);

        row.Invoking(r => r.Identity(HashKind.Md5)).Should().Throw<InvalidOperationException>().WithMessage("*Md5*");
    }

    [Theory]
    [InlineData(HashKind.None)]
    [InlineData(HashKind.Sha256)]
    public async Task IdentityOfAKindTheModelNeverStores_Throws(HashKind kind)
    {
        var hashes = await MultiHasher.HashStream(new MemoryStream("vanilla"u8.ToArray()));
        var row = await Commit(hashes, withMd5: true);

        row.Invoking(r => r.Identity(kind)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AnAttestedLookup_ExposesItsSha1AsAnIdentity()
    {
        var sha1 = Sha1Value.From(Enumerable.Range(1, 20).Select(i => (byte)i).ToArray());
        var lookup = new AttestedLookup(default, default, sha1, NexusMods.Paths.Size.From(3), DateTimeOffset.UnixEpoch);
        lookup.Identity.Should().Be(FileIdentity.Sha1(sha1));
    }

    private async Task<HashRelation.ReadOnly> Commit(MultiHash hashes, bool withMd5)
    {
        var service = _harness.CreateService(_harness.NewDatabaseLocation());
        var local = service.Local.Connection;
        using var tx = local.BeginTransaction();
        var created = new HashRelation.New(tx)
        {
            MinimalHash = hashes.MinimalHash, XxHash3 = hashes.XxHash3, Sha1 = hashes.Sha1, XxHash64 = hashes.XxHash64,
            Crc32 = hashes.Crc32, Md5 = hashes.Md5, Size = hashes.Size,
        };
        var result = await tx.Commit();
        var id = result[created.Id];
        if (withMd5) return HashRelation.Load(local.Db, id);

        // Rows from other writers can lack a digest; the model's initializer insists on all of them.
        using var retract = local.BeginTransaction();
        retract.Retract(id, HashRelation.Md5, hashes.Md5);
        await retract.Commit();
        return HashRelation.Load(local.Db, id);
    }
}
