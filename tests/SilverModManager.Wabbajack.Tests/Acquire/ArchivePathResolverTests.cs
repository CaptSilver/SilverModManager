using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Library;
using SilverModManager.Sdk.Loadouts;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Library;

namespace SilverModManager.Wabbajack.Tests.Acquire;

public class ArchivePathResolverTests(ITestOutputHelper helper) : AcquireTestBase<ArchivePathResolverTests>(helper)
{
    private readonly List<ArchivePathResolver> _resolvers = [];

    // Resolvers pin what they back up; releasing them keeps one test's pins out of another's assertions.
    public override async ValueTask DisposeAsync()
    {
        foreach (var resolver in _resolvers) resolver.Dispose();
        await base.DisposeAsync();
    }

    private static RelativePath P(string path) => RelativePath.FromUnsanitizedInput(path);

    private ArchivePathResolver Resolver(ArchivePlan plan, Loadout.ReadOnly loadout, params INestedArchiveOpener[] openers)
    {
        var resolver = new ArchivePathResolver(plan, Connection.Db, FileStore,
            new GameFileSourceLookup(loadout, Connection, FileStore, ServiceProvider.GetRequiredService<IFileHashesService>()),
            openers);
        _resolvers.Add(resolver);
        return resolver;
    }

    private async Task<(ArchivePathResolver Resolver, Hash ArchiveHash)> ResolverOverZip(Dictionary<RelativePath, byte[]> files)
    {
        await using var zip = await CreateTestArchive(files);
        var library = await RegisterLocalArchive(zip.Path);
        var hash = library.AsLibraryFile().XxHash64.Value;
        var loadout = await CreateLoadout();
        return (Resolver(new ArchivePlan([]), loadout), hash);
    }

    private static async Task<byte[]> ReadAll(ResolvedMember member)
    {
        await using var stream = await member.Open();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    [Fact]
    public async Task AMemberOfAnArchiveResolvesWithItsHashes()
    {
        var bytes = "member bytes"u8.ToArray();
        var (resolver, archive) = await ResolverOverZip(new() { [P("a/b.txt")] = bytes, [P("other.txt")] = "x"u8.ToArray() });

        var member = await resolver.Resolve(new ArchiveHashPath(archive, [P("a/b.txt")]), CancellationToken.None);

        member.StoreHash.Should().Be(bytes.xxHash3());
        member.XxHash64.Should().Be(Hash64(bytes));
        member.Size.Value.Should().Be((ulong)bytes.Length);
        (await ReadAll(member)).Should().Equal(bytes);
    }

    [Fact]
    public async Task APartIsFoundWhateverItsCaseOrSeparator()
    {
        var bytes = "member bytes"u8.ToArray();
        var (resolver, archive) = await ResolverOverZip(new() { [P("Meshes/Armor/Helm.NIF")] = bytes });

        var member = await resolver.Resolve(new ArchiveHashPath(archive, [P("meshes/armor/helm.nif")]), CancellationToken.None);

        member.StoreHash.Should().Be(bytes.xxHash3());
    }

    [Fact]
    public async Task AMemberInsideANestedZipInsideA7zResolvesThroughTwoParts()
    {
        var path = FileSystem.FromUnsanitizedFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "ZipInside7z.7z"));
        var library = await RegisterLocalArchive(path);
        var archive = library.AsLibraryFile().XxHash64.Value;
        var loadout = await CreateLoadout();
        var resolver = Resolver(new ArchivePlan([]), loadout);

        var member = await resolver.Resolve(new ArchiveHashPath(archive, [P("pack/inner.zip"), P("deep/c.txt")]), CancellationToken.None);

        (await ReadAll(member)).Should().Equal("nested!"u8.ToArray());
        member.XxHash64.Should().Be(Hash64("nested!"u8.ToArray()));
    }

    [Fact]
    public async Task AZeroPartPathToAPlainLibraryFileResolvesToThatFile()
    {
        var bytes = Enumerable.Range(0, 2000).Select(i => (byte)(i * 13)).ToArray();
        await using var plugin = await CreateTestFile("Plugin.esp", bytes);
        var loadout = await CreateLoadout();
        var added = await LibraryService.AddLocalFile(plugin.Path);
        (await FileStore.HaveFile(added.AsLibraryFile().Hash)).Should().BeTrue("the library keeps a plain file's bytes");
        var resolver = Resolver(new ArchivePlan([]), loadout);

        var member = await resolver.Resolve(new ArchiveHashPath(Hash64(bytes), []), CancellationToken.None);

        member.StoreHash.Should().Be(added.AsLibraryFile().Hash);
        member.XxHash64.Should().Be(Hash64(bytes));
        (await ReadAll(member)).Should().Equal(bytes);
    }

    [Fact]
    public async Task AZeroPartPathToAGameFileSourceResolvesToTheGameFileAndStoresIt()
    {
        var loadout = await CreateLoadout();
        var gameHash = Hash64(GameBsaBytes);
        var state = new GameFileSourceState("SkyrimSpecialEdition", GameBsa, gameHash, GameVersion);
        var plan = new ArchivePlan([new ArchivePlanRow(Entry(state, gameHash, GameBsaBytes.Length), ArchiveState.GameFile, null, null, null)]);
        var resolver = Resolver(plan, loadout);

        var member = await resolver.Resolve(new ArchiveHashPath(gameHash, []), CancellationToken.None);

        member.StoreHash.Should().Be(GameBsaBytes.xxHash3());
        member.XxHash64.Should().Be(gameHash);
        (await FileStore.HaveFile(GameBsaBytes.xxHash3())).Should().BeTrue("the game file is kept so later reads come from the store");
        (await ReadAll(member)).Should().Equal(GameBsaBytes);
    }

    [Fact]
    public async Task BytesTheResolverBacksUpStayPinnedUntilItIsDisposed()
    {
        // Bytes no other test stores: pins are process-wide and test classes run in parallel.
        var inner = System.Text.Encoding.UTF8.GetBytes($"opener output {Guid.NewGuid()}");
        var (_, archive) = await ResolverOverZip(new() { [P("textures.bsa")] = "BSA\0not really"u8.ToArray() });
        var resolver = Resolver(new ArchivePlan([]), await CreateLoadout(), new FakeOpener(inner));

        await resolver.Resolve(new ArchiveHashPath(archive, [P("textures.bsa"), P("a.dds")]), CancellationToken.None);

        FileStorePins.Pinned().Should().Contain(inner.xxHash3(), "no loadout file references the backup yet");

        resolver.Dispose();

        FileStorePins.Pinned().Should().NotContain(inner.xxHash3());
    }

    [Fact]
    public async Task BytesAlreadyInTheStoreAreCheckedOnlyOnceACollectionInProgressHasFinished()
    {
        // A collection that marked before the pin existed may still drop bytes the store has right now, so the
        // resolver must not decide "already stored" while one holds the store.
        var loadout = await CreateLoadout();
        await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(GameBsa, new MemoryStream(GameBsaBytes)), GameBsaBytes.xxHash3(), Size.FromLong(GameBsaBytes.Length))]);
        var gameHash = Hash64(GameBsaBytes);
        var state = new GameFileSourceState("SkyrimSpecialEdition", GameBsa, gameHash, GameVersion);
        var plan = new ArchivePlan([new ArchivePlanRow(Entry(state, gameHash, GameBsaBytes.Length), ArchiveState.GameFile, null, null, null)]);
        var resolver = Resolver(plan, loadout);

        Task<ResolvedMember> resolving;
        using (FileStore.WriteLock())
        {
            resolving = Task.Run(() => resolver.Resolve(new ArchiveHashPath(gameHash, []), CancellationToken.None));
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            resolving.IsCompleted.Should().BeFalse("the collection still holds the store");
        }

        (await ReadAll(await resolving)).Should().Equal(GameBsaBytes);
    }

    [Fact]
    public async Task AGameFileReadOnceIsServedFromTheStoreAfterTheLiveFileChanges()
    {
        var loadout = await CreateLoadout();
        var gameHash = Hash64(GameBsaBytes);
        var state = new GameFileSourceState("SkyrimSpecialEdition", GameBsa, gameHash, GameVersion);
        var plan = new ArchivePlan([new ArchivePlanRow(Entry(state, gameHash, GameBsaBytes.Length), ArchiveState.GameFile, null, null, null)]);
        var resolver = Resolver(plan, loadout);
        await resolver.Resolve(new ArchiveHashPath(gameHash, []), CancellationToken.None);

        var changed = (byte[])GameBsaBytes.Clone();
        changed[0] ^= 0xFF;
        await loadout.InstallationInstance.Locations.ToAbsolutePath(new GamePath(LocationId.Game, GameBsa)).WriteAllBytesAsync(changed);
        var again = await resolver.Resolve(new ArchiveHashPath(gameHash, []), CancellationToken.None);

        again.StoreHash.Should().Be(GameBsaBytes.xxHash3());
        (await ReadAll(again)).Should().Equal(GameBsaBytes);
    }

    [Fact]
    public async Task AMemberOfAGameFileIsAskedOfTheOpenerForThatFile()
    {
        var inner = "from a game archive"u8.ToArray();
        var loadout = await CreateLoadout();
        var gameHash = Hash64(GameBsaBytes);
        var state = new GameFileSourceState("SkyrimSpecialEdition", GameBsa, gameHash, GameVersion);
        var plan = new ArchivePlan([new ArchivePlanRow(Entry(state, gameHash, GameBsaBytes.Length), ArchiveState.GameFile, null, null, null)]);

        var withoutOpener = () => Resolver(plan, loadout).Resolve(new ArchiveHashPath(gameHash, [P("textures/a.dds")]), CancellationToken.None);
        (await withoutOpener.Should().ThrowAsync<MemberNotFoundException>()).Which.Message.Should().Contain(".bsa");

        var member = await Resolver(plan, loadout, new FakeOpener(inner)).Resolve(new ArchiveHashPath(gameHash, [P("textures/a.dds")]), CancellationToken.None);
        (await ReadAll(member)).Should().Equal(inner);
    }

    [Fact]
    public async Task AnOpenerThatReturnsANonSeekableStreamResolves()
    {
        var inner = Enumerable.Range(0, 50_000).Select(i => (byte)(i * 31)).ToArray();
        var (_, archive) = await ResolverOverZip(new() { [P("textures.bsa")] = "BSA\0not really"u8.ToArray() });
        var loadout = await CreateLoadout();
        var resolver = Resolver(new ArchivePlan([]), loadout, new FakeOpener(inner, seekable: false));

        var member = await resolver.Resolve(new ArchiveHashPath(archive, [P("textures.bsa"), P("a.dds")]), CancellationToken.None);

        member.Size.Value.Should().Be((ulong)inner.Length);
        member.XxHash64.Should().Be(Hash64(inner));
        (await ReadAll(member)).Should().Equal(inner);
    }

    [Fact]
    public async Task AMemberInsideABsaMemberFailsNamingTheBsa()
    {
        var (resolver, archive) = await ResolverOverZip(new() { [P("textures.bsa")] = "BSA\0not really"u8.ToArray() });

        var act = () => resolver.Resolve(new ArchiveHashPath(archive, [P("textures.bsa"), P("a.dds")]), CancellationToken.None);

        (await act.Should().ThrowAsync<MemberNotFoundException>()).Which.Message.Should().Contain(".bsa");
    }

    [Fact]
    public async Task ARegisteredOpenerIsAskedForAMemberOfANonArchiveMember()
    {
        var inner = "from the opener"u8.ToArray();
        var (_, archive) = await ResolverOverZip(new() { [P("textures.bsa")] = "BSA\0not really"u8.ToArray() });
        var loadout = await CreateLoadout();
        var resolver = Resolver(new ArchivePlan([]), loadout, new FakeOpener(inner));

        var member = await resolver.Resolve(new ArchiveHashPath(archive, [P("textures.bsa"), P("a.dds")]), CancellationToken.None);

        member.StoreHash.Should().Be(inner.xxHash3());
        member.XxHash64.Should().Be(Hash64(inner));
        (await ReadAll(member)).Should().Equal(inner);
        (await FileStore.HaveFile(inner.xxHash3())).Should().BeTrue();
    }

    [Fact]
    public async Task AWrongPartNameFails()
    {
        var (resolver, archive) = await ResolverOverZip(new() { [P("a/b.txt")] = "x"u8.ToArray() });

        var act = () => resolver.Resolve(new ArchiveHashPath(archive, [P("a/missing.txt")]), CancellationToken.None);

        (await act.Should().ThrowAsync<MemberNotFoundException>()).Which.Message.Should().Contain("a/missing.txt");
    }

    [Fact]
    public async Task AnArchiveTheLibraryDoesNotHaveFails()
    {
        var loadout = await CreateLoadout();
        var resolver = Resolver(new ArchivePlan([]), loadout);

        var act = () => resolver.Resolve(new ArchiveHashPath(Hash.From(0xABCDEF), [P("a.txt")]), CancellationToken.None);

        await act.Should().ThrowAsync<MemberNotFoundException>();
    }

    [Fact]
    public async Task ManyDirectivesAgainstOneArchiveBuildTheEntryIndexOnce()
    {
        var bytes = "member"u8.ToArray();
        var (resolver, archive) = await ResolverOverZip(new() { [P("a/b.txt")] = bytes, [P("c.txt")] = bytes });
        var path = new ArchiveHashPath(archive, [P("a/b.txt")]);

        for (var i = 0; i < 10_000; i++)
            await resolver.Resolve(path, CancellationToken.None);

        resolver.IndexesBuilt.Should().Be(1);
    }

    private sealed class FakeOpener(byte[] content, bool seekable = true) : INestedArchiveOpener
    {
        public bool CanOpen(RelativePath name) => name.Extension == new Extension(".bsa");

        public Task<Stream?> Open(Stream archive, RelativePath member, CancellationToken cancellationToken)
            => Task.FromResult<Stream?>(seekable ? new MemoryStream(content) : new ForwardOnlyStream(new MemoryStream(content)));
    }

    /// <summary>Reads like a decompressing stream: no seeking, no length, no position.</summary>
    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
