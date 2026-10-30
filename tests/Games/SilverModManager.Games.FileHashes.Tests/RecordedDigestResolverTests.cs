using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Games.FileHashes.Tests;

/// <summary>
/// Joins identities of different kinds through recorded <see cref="HashRelation"/> rows, local store first.
/// </summary>
public sealed class RecordedDigestResolverTests : IDisposable
{
    private readonly ServiceHarness _harness = new();
    private readonly IConnection _local;
    private readonly IConnection _downloaded;
    private readonly IFileHashesService _hashes = Substitute.For<IFileHashesService>();
    private int _reads;
    private readonly RecordedDigestResolver _resolver;

    public RecordedDigestResolverTests()
    {
        _local = _harness.Host.OpenInMemory("local_hashes");
        _downloaded = _harness.Host.OpenInMemory("hashes");
        _hashes.Databases.Returns(_ =>
        {
            _reads++;
            return new[] { _local.Db, _downloaded.Db };
        });
        _resolver = new RecordedDigestResolver(_hashes);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task TryConvert_Sha1ToXxHash3_ThroughTheLocalRow()
    {
        var a = await Hashed("alpha");
        await AddRow(_local, a);

        _resolver.TryConvert(FileIdentity.Sha1(a.Sha1), a.Size, HashKind.XxHash3, out var converted).Should().BeTrue();

        converted.Should().Be(FileIdentity.XxHash3(a.XxHash3));
    }

    [Fact]
    public async Task TryConvert_XxHash3ToSha1()
    {
        var a = await Hashed("alpha");
        await AddRow(_downloaded, a);

        _resolver.TryConvert(FileIdentity.XxHash3(a.XxHash3), a.Size, HashKind.Sha1, out var converted).Should().BeTrue();

        converted.Should().Be(FileIdentity.Sha1(a.Sha1));
    }

    [Fact]
    public async Task TryConvert_Md5Crc32AndXxHash64_AreJoinedToo()
    {
        var a = await Hashed("alpha");
        await AddRow(_local, a);

        _resolver.TryConvert(FileIdentity.Md5(a.Md5), a.Size, HashKind.XxHash3, out var fromMd5).Should().BeTrue();
        _resolver.TryConvert(FileIdentity.Crc32(a.Crc32), a.Size, HashKind.XxHash3, out var fromCrc).Should().BeTrue();
        _resolver.TryConvert(FileIdentity.XxHash64(a.XxHash64), a.Size, HashKind.Sha1, out var fromXx64).Should().BeTrue();

        fromMd5.Should().Be(FileIdentity.XxHash3(a.XxHash3));
        fromCrc.Should().Be(FileIdentity.XxHash3(a.XxHash3));
        fromXx64.Should().Be(FileIdentity.Sha1(a.Sha1));
    }

    [Fact]
    public async Task TryConvert_SameKind_ReturnsInputWithoutLookup()
    {
        var a = await Hashed("alpha");
        var identity = FileIdentity.Sha1(a.Sha1);

        _resolver.TryConvert(identity, a.Size, HashKind.Sha1, out var converted).Should().BeTrue();

        converted.Should().Be(identity);
        _reads.Should().Be(0);
    }

    [Fact]
    public async Task TryConvert_SizeMismatch_IsFalse()
    {
        var a = await Hashed("alpha");
        await AddRow(_local, a);

        _resolver.TryConvert(FileIdentity.Sha1(a.Sha1), Size.From(a.Size.Value + 1), HashKind.XxHash3, out _).Should().BeFalse();
    }

    [Fact]
    public async Task TryConvert_RowLacksTargetDigest_IsFalse()
    {
        var a = await Hashed("alpha");
        await AddRow(_local, a, withMd5: false);

        _resolver.TryConvert(FileIdentity.Sha1(a.Sha1), a.Size, HashKind.Md5, out _).Should().BeFalse();
    }

    [Fact]
    public async Task TryConvert_LocalRowWinsOverDownloaded()
    {
        var a = await Hashed("alpha");
        var b = await Hashed("beta");
        await AddRow(_downloaded, a);
        await AddRow(_local, a, xxHash3: b.XxHash3);

        _resolver.TryConvert(FileIdentity.Sha1(a.Sha1), a.Size, HashKind.XxHash3, out var converted).Should().BeTrue();

        converted.Should().Be(FileIdentity.XxHash3(b.XxHash3));
    }

    [Fact]
    public async Task Resolve_PrefersXxHash3_AndAnswersSame()
    {
        var a = await Hashed("alpha");
        await AddRow(_local, a);

        _resolver.Resolve(FileIdentity.Sha1(a.Sha1), FileIdentity.XxHash3(a.XxHash3), a.Size).Should().Be(IdentityMatch.Same);
        _resolver.Resolve(FileIdentity.XxHash3(a.XxHash3), FileIdentity.Sha1(a.Sha1), a.Size).Should().Be(IdentityMatch.Same);
    }

    [Fact]
    public async Task Resolve_SameKind_NeedsNoRow()
    {
        var a = await Hashed("alpha");
        var b = await Hashed("beta");

        _resolver.Resolve(FileIdentity.Sha1(a.Sha1), FileIdentity.Sha1(b.Sha1), a.Size).Should().Be(IdentityMatch.Different);
    }

    [Fact]
    public async Task Resolve_NoRow_IsUnknown()
    {
        var a = await Hashed("alpha");

        _resolver.Resolve(FileIdentity.Sha1(a.Sha1), FileIdentity.XxHash3(a.XxHash3), a.Size).Should().Be(IdentityMatch.Unknown);
    }

    [Fact]
    public async Task Resolve_DifferentBytes_IsDifferent()
    {
        var a = await Hashed("alpha");
        var b = await Hashed("beta");
        await AddRow(_local, a);

        _resolver.Resolve(FileIdentity.Sha1(a.Sha1), FileIdentity.XxHash3(b.XxHash3), a.Size).Should().Be(IdentityMatch.Different);
    }

    [Fact]
    public async Task Resolve_OnlyTheXxHash3SideHasARow_ItsOtherDigestSettlesTheAnswer()
    {
        var a = await Hashed("alpha");
        var b = await Hashed("beta");
        await AddRow(_local, b, size: a.Size);

        _resolver.Resolve(FileIdentity.Sha1(a.Sha1), FileIdentity.XxHash3(b.XxHash3), a.Size).Should().Be(IdentityMatch.Different);
        _resolver.Resolve(FileIdentity.XxHash3(b.XxHash3), FileIdentity.Sha1(a.Sha1), a.Size).Should().Be(IdentityMatch.Different);
        _resolver.Resolve(FileIdentity.Sha1(b.Sha1), FileIdentity.XxHash3(b.XxHash3), a.Size).Should().Be(IdentityMatch.Same);
    }

    [Fact]
    public async Task Resolve_TwoRowsShareTheSha1_JoinsThroughTheOneWhoseXxHash3IsAsked()
    {
        var a = await Hashed("alpha");
        var b = await Hashed("beta");
        await AddRow(_local, a, xxHash3: b.XxHash3); // listed first, so it is the SHA-1's first row
        await AddRow(_local, a);

        _resolver.Resolve(FileIdentity.Sha1(a.Sha1), FileIdentity.XxHash3(a.XxHash3), a.Size).Should().Be(IdentityMatch.Same);
        _resolver.Resolve(FileIdentity.XxHash3(a.XxHash3), FileIdentity.Sha1(a.Sha1), a.Size).Should().Be(IdentityMatch.Same);
    }

    [Fact]
    public async Task Resolve_TwoKindsNeitherXxHash3_ConvertsLeftToRight()
    {
        var a = await Hashed("alpha");
        await AddRow(_local, a);

        _resolver.Resolve(FileIdentity.Sha1(a.Sha1), FileIdentity.Md5(a.Md5), a.Size).Should().Be(IdentityMatch.Same);
    }

    [Fact]
    public async Task Resolve_TwoKindsNeitherXxHash3_ConvertsRightToLeftWhenOnlyTheRightHasAUsableRow()
    {
        var a = await Hashed("alpha");
        var b = await Hashed("beta");
        await AddRow(_local, a, withMd5: false);
        await AddRow(_local, b, size: a.Size);

        // The row found by the SHA-1 has no MD5 to offer, so the answer has to come from the row found by the MD5.
        _resolver.TryConvert(FileIdentity.Sha1(a.Sha1), a.Size, HashKind.Md5, out _).Should().BeFalse();
        _resolver.Resolve(FileIdentity.Sha1(a.Sha1), FileIdentity.Md5(b.Md5), a.Size).Should().Be(IdentityMatch.Different);
    }

    [Fact]
    public async Task Scoped_AsksTheIndexOnce()
    {
        var a = await Hashed("alpha");
        await AddRow(_local, a);
        var scoped = _resolver.Scoped();

        scoped.TryConvert(FileIdentity.Sha1(a.Sha1), a.Size, HashKind.XxHash3, out var first).Should().BeTrue();
        scoped.TryConvert(FileIdentity.Sha1(a.Sha1), a.Size, HashKind.XxHash3, out var second).Should().BeTrue();
        scoped.Resolve(FileIdentity.Sha1(a.Sha1), FileIdentity.XxHash3(a.XxHash3), a.Size).Should().Be(IdentityMatch.Same);

        second.Should().Be(first);
        _reads.Should().Be(1);
    }

    [Fact]
    public async Task Scoped_RemembersAMissToo()
    {
        var a = await Hashed("alpha");
        var scoped = _resolver.Scoped();

        scoped.TryConvert(FileIdentity.Sha1(a.Sha1), a.Size, HashKind.XxHash3, out _).Should().BeFalse();
        scoped.TryConvert(FileIdentity.Sha1(a.Sha1), a.Size, HashKind.XxHash3, out _).Should().BeFalse();

        _reads.Should().Be(1);
    }

    [Fact]
    public async Task StoreKeyCandidates_ListsEverySizeMatchingRow_AsXxHash3Identities()
    {
        var a = await Hashed("alpha");
        var b = await Hashed("beta");
        var c = await Hashed("gamma");
        await AddRow(_local, a);
        await AddRow(_downloaded, a, xxHash3: b.XxHash3);
        await AddRow(_downloaded, a, xxHash3: a.XxHash3); // the same key twice is listed once
        await AddRow(_downloaded, a, xxHash3: c.XxHash3, size: Size.From(a.Size.Value + 1)); // another size is other bytes

        var candidates = _resolver.StoreKeyCandidates(FileIdentity.Sha1(a.Sha1), a.Size).ToList();

        candidates.Should().Equal(FileIdentity.XxHash3(a.XxHash3), FileIdentity.XxHash3(b.XxHash3));
    }

    [Fact]
    public async Task StoreKeyCandidates_NoRow_IsEmpty()
    {
        var a = await Hashed("alpha");

        _resolver.StoreKeyCandidates(FileIdentity.Sha1(a.Sha1), a.Size).Should().BeEmpty();
    }

    [Fact]
    public async Task StoreKeyAsync_ReturnsTheCandidateTheStoreHolds_EvenWhenItIsTheSecond()
    {
        var a = await Hashed("alpha");
        var b = await Hashed("beta");
        await AddRow(_local, a);
        await AddRow(_downloaded, a, xxHash3: b.XxHash3);

        var key = await _resolver.StoreKeyAsync(StoreHolding(b.XxHash3), FileIdentity.Sha1(a.Sha1), a.Size);

        key.Should().Be(FileIdentity.XxHash3(b.XxHash3));
    }

    [Fact]
    public async Task StoreKeyAsync_XxHash3_IsItselfWhenHeld()
    {
        var a = await Hashed("alpha");

        var key = await _resolver.StoreKeyAsync(StoreHolding(a.XxHash3), FileIdentity.XxHash3(a.XxHash3), a.Size);

        key.Should().Be(FileIdentity.XxHash3(a.XxHash3));
    }

    [Fact]
    public async Task StoreKeyAsync_NothingHeld_IsNull()
    {
        var a = await Hashed("alpha");
        await AddRow(_local, a);

        var key = await _resolver.StoreKeyAsync(StoreHolding(), FileIdentity.Sha1(a.Sha1), a.Size);

        key.Should().BeNull();
    }

    private static FileIdentity IdentityOf(MultiHash hashes, HashKind kind) => FileIdentity.Of(hashes, kind);

    private static async Task<List<MultiHash>> HashedMany(string prefix, int count)
    {
        List<MultiHash> all = [];
        for (var i = 0; i < count; i++) all.Add(await Hashed($"{prefix}-{i}"));
        return all;
    }

    [Fact]
    public async Task ScopedOverManyUnrecordedIdentities_QueriesOncePerKindAndDatabase_NotPerIdentity()
    {
        var files = await HashedMany("unrecorded", 300);
        var expected = files.SelectMany(f => new[] { FileIdentity.Sha1(f.Sha1), FileIdentity.XxHash3(f.XxHash3) }).ToList();

        var scoped = _resolver.Scoped(expected);
        foreach (var f in files)
        {
            scoped.Resolve(FileIdentity.Sha1(f.Sha1), FileIdentity.XxHash3(f.XxHash3), f.Size).Should().Be(IdentityMatch.Unknown);
            scoped.StoreKeyCandidates(FileIdentity.Sha1(f.Sha1), f.Size).Should().BeEmpty();
        }

        // Two kinds (SHA-1 and xxHash3) in two databases.
        _resolver.QueriesIssued.Should().BeLessThanOrEqualTo(4);
    }

    [Theory]
    [InlineData(HashKind.Sha1)]
    [InlineData(HashKind.Md5)]
    [InlineData(HashKind.XxHash64)]
    [InlineData(HashKind.Crc32)]
    public async Task ScopedWithExpectedIdentities_AnswersLikeTheUnscopedResolver_ForEveryKind(HashKind kind)
    {
        var files = await HashedMany($"mixed-{kind}", 100);
        for (var i = 0; i < files.Count; i += 2) await AddRow(i % 4 == 0 ? _local : _downloaded, files[i]);
        var expected = files.SelectMany(f => new[] { IdentityOf(f, kind), FileIdentity.XxHash3(f.XxHash3) }).ToList();

        var scoped = _resolver.Scoped(expected);

        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            var recorded = i % 2 == 0;
            scoped.TryConvert(IdentityOf(f, kind), f.Size, HashKind.XxHash3, out var viaScope).Should().Be(recorded, $"file {i} as {kind}");
            scoped.TryConvert(FileIdentity.XxHash3(f.XxHash3), f.Size, kind, out var back).Should().Be(recorded, $"file {i} back to {kind}");
            scoped.Resolve(IdentityOf(f, kind), FileIdentity.XxHash3(f.XxHash3), f.Size)
                .Should().Be(recorded ? IdentityMatch.Same : IdentityMatch.Unknown);
            if (recorded)
            {
                viaScope.Should().Be(FileIdentity.XxHash3(f.XxHash3));
                back.Should().Be(IdentityOf(f, kind));
            }
        }
    }

    [Fact]
    public async Task ScopedWithExpectedIdentities_StillAnswersAnIdentityItWasNotToldAbout()
    {
        var files = await HashedMany("told", 100);
        var late = await Hashed("late");
        await AddRow(_local, late);

        var scoped = _resolver.Scoped(files.Select(f => FileIdentity.Sha1(f.Sha1)).ToList());

        scoped.Resolve(FileIdentity.Sha1(late.Sha1), FileIdentity.XxHash3(late.XxHash3), late.Size).Should().Be(IdentityMatch.Same);
    }

    [Fact]
    public async Task ScopedWithExpectedIdentities_ASmallSetIsLookedUpOnDemand()
    {
        var a = await Hashed("alpha");

        var scoped = _resolver.Scoped([FileIdentity.Sha1(a.Sha1)]);

        _resolver.QueriesIssued.Should().Be(0);
        scoped.Resolve(FileIdentity.Sha1(a.Sha1), FileIdentity.XxHash3(a.XxHash3), a.Size).Should().Be(IdentityMatch.Unknown);
    }

    private static IFileStore StoreHolding(params Hash[] held)
    {
        var store = Substitute.For<IFileStore>();
        var heldKeys = held.Select(FileIdentity.XxHash3).ToHashSet();
        store.HaveFile(default).ReturnsForAnyArgs(call => ValueTask.FromResult(heldKeys.Contains(call.Arg<FileIdentity>())));
        return store;
    }

    private static async Task<MultiHash> Hashed(string content) => await MultiHasher.HashStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)));

    /// <summary>
    /// Writes a row of <paramref name="hashes"/>, optionally with another xxHash3 or size, which is how two
    /// rows come to share a SHA-1 without being the same bytes' key.
    /// </summary>
    private static async Task AddRow(IConnection store, MultiHash hashes, bool withMd5 = true, Hash? xxHash3 = null, Size? size = null)
    {
        using var tx = store.BeginTransaction();
        var created = new HashRelation.New(tx)
        {
            MinimalHash = hashes.MinimalHash, XxHash3 = xxHash3 ?? hashes.XxHash3, Sha1 = hashes.Sha1, XxHash64 = hashes.XxHash64,
            Crc32 = hashes.Crc32, Md5 = hashes.Md5, Size = size ?? hashes.Size,
        };
        var result = await tx.Commit();
        if (withMd5) return;

        // Rows from other writers can lack a digest; the model's initializer insists on all of them.
        using var retract = store.BeginTransaction();
        retract.Retract(result[created.Id], HashRelation.Md5, hashes.Md5);
        await retract.Commit();
    }
}
