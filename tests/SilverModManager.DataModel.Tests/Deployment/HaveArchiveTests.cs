using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.DataModel.Tests.Deployment;

/// <summary>
/// The file store is keyed by xxHash3. A SHA-1 identity is archived only when a relation computed from bytes joins
/// it to an xxHash3 the store holds.
/// </summary>
public class HaveArchiveTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<HaveArchiveTests>(helper)
{
    private sealed class Probe(IServiceProvider provider) : DefaultSynchronizer(provider)
    {
        public bool Have(FileIdentity identity, Size size) => HaveArchive(identity, size);
    }

    private Probe CreateProbe() => new(ServiceProvider);

    private async Task<MultiHash> Archive(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hashes = await MultiHasher.HashStream(new MemoryStream(bytes));
        await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory("file", new MemoryStream(bytes)), hashes.XxHash3, hashes.Size)]);
        return hashes;
    }

    private async Task Relate(MultiHash hashes)
    {
        var local = ((StubbedFileHasherService)ServiceProvider.GetRequiredService<IFileHashesService>()).Local.Connection;
        using var tx = local.BeginTransaction();
        _ = new HashRelation.New(tx)
        {
            MinimalHash = hashes.MinimalHash,
            XxHash3 = hashes.XxHash3,
            Sha1 = hashes.Sha1,
            XxHash64 = hashes.XxHash64,
            Md5 = hashes.Md5,
            Size = hashes.Size,
            Crc32 = hashes.Crc32,
        };
        await tx.Commit();
    }

    [Fact]
    public async Task AnXxHash3IdentityIsArchivedWhenTheStoreHoldsIt()
    {
        var archived = await Archive("archived");
        var missing = await MultiHasher.HashStream(new MemoryStream("never archived"u8.ToArray()));
        var probe = CreateProbe();

        probe.Have(FileIdentity.XxHash3(archived.XxHash3), archived.Size).Should().BeTrue();
        probe.Have(FileIdentity.XxHash3(missing.XxHash3), missing.Size).Should().BeFalse();
    }

    [Fact]
    public async Task ASha1IdentityIsArchivedThroughARelationToAnArchivedXxHash3()
    {
        var hashes = await Archive("vanilla");
        await Relate(hashes);
        var probe = CreateProbe();

        probe.Have(FileIdentity.Sha1(hashes.Sha1), hashes.Size).Should().BeTrue();
    }

    [Fact]
    public async Task ASha1IdentityOfAnotherSizeIsNotTheRelatedFile()
    {
        var hashes = await Archive("vanilla");
        await Relate(hashes);

        CreateProbe().Have(FileIdentity.Sha1(hashes.Sha1), Size.From(hashes.Size.Value + 1)).Should().BeFalse();
    }

    [Fact]
    public async Task ASha1IdentityWhoseRelatedBytesAreNotArchivedIsNotArchived()
    {
        var hashes = await MultiHasher.HashStream(new MemoryStream("related but not archived"u8.ToArray()));
        await Relate(hashes);

        CreateProbe().Have(FileIdentity.Sha1(hashes.Sha1), hashes.Size).Should().BeFalse();
    }

    [Fact]
    public async Task ASha1IdentityWithNoRelationIsNotArchived()
    {
        var hashes = await Archive("archived but unrelated");

        CreateProbe().Have(FileIdentity.Sha1(hashes.Sha1), hashes.Size).Should().BeFalse("nothing joins the two kinds without bytes the app hashed");
    }

    [Fact]
    public async Task AnyOtherKindIsNeverArchived()
    {
        var hashes = await Archive("vanilla");
        await Relate(hashes);

        CreateProbe().Have(FileIdentity.Of(HashKind.Md5, hashes.Md5.AsSpan()), hashes.Size).Should().BeFalse();
        CreateProbe().Have(FileIdentity.None, hashes.Size).Should().BeFalse();
    }
}
