using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        => ResolverOver(FileStore, plan, loadout, openers);

    private ArchivePathResolver ResolverOver(IFileStore store, ArchivePlan plan, Loadout.ReadOnly loadout, params INestedArchiveOpener[] openers)
        => ResolverOver(store, new ListLogger<ArchivePathResolver>(), TimeSpan.FromSeconds(30), plan, loadout, openers);

    private ArchivePathResolver ResolverOver(IFileStore store, ILogger logger, TimeSpan drainTimeout, ArchivePlan plan, Loadout.ReadOnly loadout, params INestedArchiveOpener[] openers)
    {
        var resolver = new ArchivePathResolver(plan, Connection.Db, store,
            new GameFileSourceLookup(loadout, Connection, store, ServiceProvider.GetRequiredService<IFileIdentityResolver>(), ServiceProvider.GetRequiredService<IFileHashesService>()),
            openers, logger)
        {
            CopyDrainTimeout = drainTimeout,
        };
        _resolvers.Add(resolver);
        return resolver;
    }

    private async Task<(ArchivePathResolver Resolver, FileIdentity ArchiveHash)> ResolverOverZip(Dictionary<RelativePath, byte[]> files)
    {
        await using var zip = await CreateTestArchive(files);
        var library = await RegisterLocalArchive(zip.Path);
        var hash = library.AsLibraryFile().XxHash64Identity()!.Value;
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

        member.StoreIdentity.Should().Be(FileIdentity.XxHash3(bytes.xxHash3()));
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

        member.StoreIdentity.Should().Be(FileIdentity.XxHash3(bytes.xxHash3()));
    }

    [Fact]
    public async Task AMemberInsideANestedZipInsideA7zResolvesThroughTwoParts()
    {
        var path = FileSystem.FromUnsanitizedFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "ZipInside7z.7z"));
        var library = await RegisterLocalArchive(path);
        var archive = library.AsLibraryFile().XxHash64Identity()!.Value;
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
        var added = await LibraryService.AddLocalFile(plugin.Path, UnassignedAcquisition.Instance);
        (await FileStore.HaveFile(FileIdentity.XxHash3(added.AsLibraryFile().Hash))).Should().BeTrue("the library keeps a plain file's bytes");
        var resolver = Resolver(new ArchivePlan([]), loadout);

        var member = await resolver.Resolve(new ArchiveHashPath(Hash64(bytes), []), CancellationToken.None);

        member.StoreIdentity.Should().Be(added.AsLibraryFile().Identity);
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

        member.StoreIdentity.Should().Be(FileIdentity.XxHash3(GameBsaBytes.xxHash3()));
        member.XxHash64.Should().Be(gameHash);
        (await FileStore.HaveFile(FileIdentity.XxHash3(GameBsaBytes.xxHash3()))).Should().BeTrue("the game file is kept so later reads come from the store");
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

        FileStorePins.Pinned().Should().Contain(FileIdentity.XxHash3(inner.xxHash3()), "no loadout file references the backup yet");

        resolver.Dispose();

        FileStorePins.Pinned().Should().NotContain(FileIdentity.XxHash3(inner.xxHash3()));
    }

    [Fact]
    public async Task BytesAlreadyInTheStoreAreCheckedOnlyOnceACollectionInProgressHasFinished()
    {
        // A collection that marked before the pin existed may still drop bytes the store has right now, so the
        // resolver must not decide "already stored" while one holds the store.
        var loadout = await CreateLoadout();
        await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(GameBsa, new MemoryStream(GameBsaBytes)), FileIdentity.XxHash3(GameBsaBytes.xxHash3()), Size.FromLong(GameBsaBytes.Length))]);
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

        again.StoreIdentity.Should().Be(FileIdentity.XxHash3(GameBsaBytes.xxHash3()));
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

        member.StoreIdentity.Should().Be(FileIdentity.XxHash3(inner.xxHash3()));
        member.XxHash64.Should().Be(Hash64(inner));
        (await ReadAll(member)).Should().Equal(inner);
        (await FileStore.HaveFile(FileIdentity.XxHash3(inner.xxHash3()))).Should().BeTrue();
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

        var act = () => resolver.Resolve(new ArchiveHashPath(Xx64.From(0xABCDEF), [P("a.txt")]), CancellationToken.None);

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

    [Fact]
    public async Task AMemberAnOpenerYieldsIsOpenedOncePerResolve()
    {
        var inner = System.Text.Encoding.UTF8.GetBytes($"opened once {Guid.NewGuid()}");
        var (_, archive) = await ResolverOverZip(new() { [P("textures.bsa")] = "BSA\0not really"u8.ToArray() });
        var opener = new CountingOpener(inner);
        var resolver = Resolver(new ArchivePlan([]), await CreateLoadout(), opener);

        var member = await resolver.Resolve(new ArchiveHashPath(archive, [P("textures.bsa"), P("a.dds")]), CancellationToken.None);

        opener.Opens.Should().Be(1, "hashing and backing up read the same stream, so an archive's index is parsed once for the member");
        (await ReadAll(member)).Should().Equal(inner);
    }

    [Fact]
    public async Task ResolvingTheSameOpenerMemberFromManyThreadsOpensItOnce()
    {
        var inner = System.Text.Encoding.UTF8.GetBytes($"shared {Guid.NewGuid()}");
        var (_, archive) = await ResolverOverZip(new() { [P("textures.bsa")] = "BSA\0not really"u8.ToArray() });
        var opener = new CountingOpener(inner);
        var resolver = Resolver(new ArchivePlan([]), await CreateLoadout(), opener);
        var path = new ArchiveHashPath(archive, [P("textures.bsa"), P("a.dds")]);

        var members = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => resolver.Resolve(path, CancellationToken.None))));

        opener.Opens.Should().Be(1);
        members.Select(m => m.StoreIdentity).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task DifferentMembersOfAnArchiveAreOpenedAtTheSameTime()
    {
        var (_, archive) = await ResolverOverZip(new() { [P("textures.bsa")] = "BSA\0not really"u8.ToArray() });
        var opener = new CountingOpener(System.Text.Encoding.UTF8.GetBytes($"parallel {Guid.NewGuid()}"), rendezvous: 2);
        var resolver = Resolver(new ArchivePlan([]), await CreateLoadout(), opener);

        // Each open waits for the other, so this only finishes when the resolver lets both in at once.
        var both = Task.WhenAll(
            resolver.Resolve(new ArchiveHashPath(archive, [P("textures.bsa"), P("a.dds")]), CancellationToken.None),
            resolver.Resolve(new ArchiveHashPath(archive, [P("textures.bsa"), P("b.dds")]), CancellationToken.None));

        (await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(20)))).Should().BeSameAs(both, "one open must not wait for another's to finish");
        await both;
    }

    [Fact]
    public async Task LibraryFilesWithoutARecordedHashAreBackfilledInOneCommit()
    {
        var files = Enumerable.Range(0, 20).ToDictionary(i => P($"f/{i}.txt"), i => System.Text.Encoding.UTF8.GetBytes($"backfill {i} {Guid.NewGuid()}"));
        var (_, archive) = await ResolverOverZip(files);
        var locator = new LibraryArchiveLocator();
        var members = files.Values.Select(b => locator.FindByXxHash64(Connection.Db, Hash64(b))!.Value).ToArray();
        using (var tx = Connection.BeginTransaction())
        {
            foreach (var member in members) tx.Retract(member.Id, LibraryFile.XxHash64, LibraryFile.XxHash64.Get(member));
            await tx.Commit();
        }

        // Made after the hashes are gone: a resolver reads the database as it was when it was created.
        var resolver = Resolver(new ArchivePlan([]), await CreateLoadout());
        var commits = 0;
        using var revisions = Connection.Revisions.Subscribe(_ => Interlocked.Increment(ref commits));
        await Task.Delay(200, TestContext.Current.CancellationToken); // the current revision is delivered to a new subscriber
        var before = commits;

        foreach (var path in files.Keys)
            await resolver.Resolve(new ArchiveHashPath(archive, [path]), CancellationToken.None);
        await resolver.FlushDigests(CancellationToken.None);

        (commits - before).Should().BeLessThanOrEqualTo(1, "the hashes are written together, not one commit per file");
        foreach (var member in members)
            LibraryFile.Load(Connection.Db, member.Id).XxHash64Identity().Should().NotBeNull();
    }

    [Fact]
    public async Task DisposingStopsACopyStillInProgressAfterTheCallerGaveUp()
    {
        var counting = new CountingBackups(FileStore);
        var (_, archive) = await ResolverOverZip(new() { [P("textures.bsa")] = "BSA\0not really"u8.ToArray() });
        var hanging = new HangingOpener();
        var resolver = ResolverOver(counting, new ArchivePlan([]), await CreateLoadout(), hanging);
        using var caller = new CancellationTokenSource();
        var resolving = resolver.Resolve(new ArchiveHashPath(archive, [P("textures.bsa"), P("a.dds")]), caller.Token);
        await hanging.Reading.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        await caller.CancelAsync();
        (await Record.ExceptionAsync(() => resolving)).Should().BeAssignableTo<OperationCanceledException>();
        await resolver.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        hanging.Cancelled.Should().BeTrue("the copy belongs to the resolver, which stops it when it is disposed");
        counting.Calls.Should().Be(0, "nothing may be backed up once the job has returned");
    }

    [Fact]
    public async Task DisposingStopsABackUpStillInProgressAfterTheCallerGaveUp()
    {
        var slow = new SlowBackups(FileStore);
        var inner = System.Text.Encoding.UTF8.GetBytes($"opener output {Guid.NewGuid()}");
        var (_, archive) = await ResolverOverZip(new() { [P("textures.bsa")] = "BSA\0not really"u8.ToArray() });
        var resolver = ResolverOver(slow, new ArchivePlan([]), await CreateLoadout(), new FakeOpener(inner));
        using var caller = new CancellationTokenSource();
        var resolving = resolver.Resolve(new ArchiveHashPath(archive, [P("textures.bsa"), P("a.dds")]), caller.Token);
        await slow.Started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        await caller.CancelAsync();
        (await Record.ExceptionAsync(() => resolving)).Should().BeAssignableTo<OperationCanceledException>();
        await resolver.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        slow.Cancelled.Should().BeTrue("the back up is told to stop, not left running against the store");
        FileStorePins.Pinned().Should().NotContain(FileIdentity.XxHash3(inner.xxHash3()));
    }

    [Fact]
    public async Task DisposingGivesUpOnACopyThatIgnoresItsTokenAndSaysWhichOne()
    {
        var kept = System.Text.Encoding.UTF8.GetBytes($"already stored {Guid.NewGuid()}");
        var (_, archive) = await ResolverOverZip(new() { [P("textures.bsa")] = "BSA\0not really"u8.ToArray() });
        var deaf = new DeafOpener(kept);
        var log = new ListLogger<ArchivePathResolver>();
        var resolver = ResolverOver(FileStore, log, TimeSpan.FromMilliseconds(200), new ArchivePlan([]), await CreateLoadout(), deaf);
        await resolver.Resolve(new ArchiveHashPath(archive, [P("textures.bsa"), P("done.dds")]), CancellationToken.None);
        FileStorePins.Pinned().Should().Contain(FileIdentity.XxHash3(kept.xxHash3()), "the finished copy is pinned until the resolver is disposed");
        deaf.Hang = true;
        using var caller = new CancellationTokenSource();
        var resolving = resolver.Resolve(new ArchiveHashPath(archive, [P("textures.bsa"), P("stuck.dds")]), caller.Token);
        await deaf.Reading.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await caller.CancelAsync();
        (await Record.ExceptionAsync(() => resolving)).Should().BeAssignableTo<OperationCanceledException>();

        await resolver.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        log.Messages(LogLevel.Warning).Should().ContainSingle(m => m.Contains("stuck.dds"), "the log names the copy still running");
        FileStorePins.Pinned().Should().NotContain(FileIdentity.XxHash3(kept.xxHash3()), "the pins are released even though a copy never ended");
        deaf.Release.SetResult();
    }

    [Fact]
    public async Task ABackUpThatEndsAfterDisposalIsLoggedAndLeavesNothingPinned()
    {
        var inner = System.Text.Encoding.UTF8.GetBytes($"late bytes {Guid.NewGuid()}");
        var deaf = new DeafBackups(FileStore);
        var log = new ListLogger<ArchivePathResolver>();
        var (_, archive) = await ResolverOverZip(new() { [P("textures.bsa")] = "BSA\0not really"u8.ToArray() });
        var resolver = ResolverOver(deaf, log, TimeSpan.FromMilliseconds(200), new ArchivePlan([]), await CreateLoadout(), new FakeOpener(inner));
        using var caller = new CancellationTokenSource();
        var resolving = resolver.Resolve(new ArchiveHashPath(archive, [P("textures.bsa"), P("a.dds")]), caller.Token);
        await deaf.Started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await caller.CancelAsync();
        (await Record.ExceptionAsync(() => resolving)).Should().BeAssignableTo<OperationCanceledException>();
        await resolver.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The warning for giving up waiting also names the key; only the copy ending later says "ended after".
        log.Messages(LogLevel.Warning).Should().NotContain(m => m.Contains("ended after"));

        deaf.Release.SetResult();
        // The message is written when the copy ends, so waiting for it also lets the late back up finish before teardown.
        await Eventually(() => log.Messages(LogLevel.Warning).Any(m => m.Contains("ended after") && m.Contains("a.dds")));

        FileStorePins.Pinned().Should().NotContain(FileIdentity.XxHash3(inner.xxHash3()), "a copy that ends late does not take a pin nothing would release");
    }

    [Fact]
    public async Task ManyMembersAreBackedUpTogetherNotOneBackupEach()
    {
        var (_, archive) = await ResolverOverZip(new() { [P("textures.bsa")] = "BSA\0not really"u8.ToArray() });
        var store = new CountingBackups(FileStore);
        var resolver = ResolverOver(store, new ArchivePlan([]), await CreateLoadout(), new NamedMemberOpener());
        var names = Enumerable.Range(0, 200).Select(i => $"textures/{Guid.NewGuid()}-{i}.dds").ToArray();

        var members = await Task.WhenAll(names.Select(name => resolver.Resolve(new ArchiveHashPath(archive, [P("textures.bsa"), P(name)]), CancellationToken.None)));

        for (var i = 0; i < names.Length; i++)
            (await ReadAll(members[i])).Should().Equal(System.Text.Encoding.UTF8.GetBytes(names[i]));
        store.Calls.Should().BeLessThan(names.Length / 3, "each backup is a file in the store and a rebuild of its index");
    }

    [Fact]
    public async Task AFailedBackupFailsEveryMemberInItsBatchAndIsNotRemembered()
    {
        var (_, archive) = await ResolverOverZip(new() { [P("textures.bsa")] = "BSA\0not really"u8.ToArray() });
        var store = new FailingBackups(FileStore) { Fail = true };
        var resolver = ResolverOver(store, new ArchivePlan([]), await CreateLoadout(), new NamedMemberOpener());
        var names = Enumerable.Range(0, 5).Select(i => $"textures/{Guid.NewGuid()}-{i}.dds").ToArray();

        var failures = await Task.WhenAll(names.Select(name => Record.ExceptionAsync(
            () => resolver.Resolve(new ArchiveHashPath(archive, [P("textures.bsa"), P(name)]), CancellationToken.None)).AsTask()));

        failures.Should().OnlyContain(failure => failure is InvalidOperationException);

        store.Fail = false;
        var again = await resolver.Resolve(new ArchiveHashPath(archive, [P("textures.bsa"), P(names[0])]), CancellationToken.None);
        (await ReadAll(again)).Should().Equal(System.Text.Encoding.UTF8.GetBytes(names[0]));
    }

    [Fact]
    public async Task AnArchiveIsOpenedOnceForAllTheMembersReadFromIt()
    {
        var (_, archive) = await ResolverOverZip(new() { [P("textures.bsa")] = "BSA\0not really"u8.ToArray() });
        var opener = new NamedMemberOpener();
        var resolver = Resolver(new ArchivePlan([]), await CreateLoadout(), opener);

        for (var i = 0; i < 30; i++)
            await resolver.Resolve(new ArchiveHashPath(archive, [P("textures.bsa"), P($"textures/{Guid.NewGuid()}-{i}.dds")]), CancellationToken.None);

        opener.ArchivesOpened.Should().Be(1);
        opener.ArchivesDisposed.Should().Be(0, "the resolver keeps it for the next member");

        await resolver.DisposeAsync();

        opener.ArchivesDisposed.Should().Be(1);
    }

    [Fact]
    public async Task OnlyAFewOpenedArchivesAreKeptAtOnce()
    {
        const int Containers = 12;
        var hashes = new List<FileIdentity>();
        for (var i = 0; i < Containers; i++)
        {
            await using var zip = await CreateTestArchive(new Dictionary<RelativePath, byte[]> { [P("textures.bsa")] = System.Text.Encoding.UTF8.GetBytes($"BSA\0 container {Guid.NewGuid()}") });
            hashes.Add((await RegisterLocalArchive(zip.Path)).AsLibraryFile().XxHash64Identity()!.Value);
        }

        // Made once the archives are in the library: a resolver reads the database as it was when it was made.
        var opener = new NamedMemberOpener();
        var resolver = Resolver(new ArchivePlan([]), await CreateLoadout(), opener);
        foreach (var hash in hashes)
            await resolver.Resolve(new ArchiveHashPath(hash, [P("textures.bsa"), P("textures/a.dds")]), CancellationToken.None);

        opener.ArchivesOpened.Should().Be(Containers);
        (opener.ArchivesOpened - opener.ArchivesDisposed).Should().BeLessThan(Containers, "an archive nothing is reading is let go once there are too many");
        (opener.ArchivesOpened - opener.ArchivesDisposed).Should().BeLessThanOrEqualTo(8);

        await resolver.DisposeAsync();

        opener.ArchivesDisposed.Should().Be(Containers);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the condition never became true");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>A store whose back up waits to be released and takes no notice of its token.</summary>
    private sealed class DeafBackups(IFileStore inner) : ForwardingFileStore(inner)
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task BackupFiles(IEnumerable<ArchivedFileEntry> backups, bool deduplicate = true, CancellationToken token = default, IProgress<double>? progress = null)
        {
            Started.TrySetResult();
            await Release.Task;
            await base.BackupFiles(backups, deduplicate, CancellationToken.None, progress);
        }
    }

    /// <summary>Yields a small file at once, until told to hang; a hanging open waits to be released and ignores its token.</summary>
    private sealed class DeafOpener(byte[] content) : INestedArchiveOpener
    {
        public bool Hang { get; set; }
        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CanOpen(RelativePath name) => name.Extension == new Extension(".bsa");

        public async Task<Stream?> Open(Stream archive, RelativePath member, CancellationToken cancellationToken)
        {
            if (!Hang) return new MemoryStream(content);
            Reading.TrySetResult();
            await Release.Task;
            return new MemoryStream(content);
        }
    }

    private sealed class SlowBackups(IFileStore inner) : ForwardingFileStore(inner)
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }

        public override async Task BackupFiles(IEnumerable<ArchivedFileEntry> backups, bool deduplicate = true, CancellationToken token = default, IProgress<double>? progress = null)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }
        }
    }

    /// <summary>Hands back a stream whose first read waits until it is cancelled.</summary>
    private sealed class HangingOpener : INestedArchiveOpener
    {
        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }

        public bool CanOpen(RelativePath name) => name.Extension == new Extension(".bsa");

        public Task<Stream?> Open(Stream archive, RelativePath member, CancellationToken cancellationToken) => Task.FromResult<Stream?>(new HangingStream(this));

        private sealed class HangingStream(HangingOpener owner) : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                owner.Reading.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    owner.Cancelled = true;
                    throw;
                }
                return 0;
            }
        }
    }

    private sealed class CountingOpener(byte[] content, int rendezvous = 0) : INestedArchiveOpener
    {
        private int _opens;
        private readonly TaskCompletionSource _met = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Opens => Volatile.Read(ref _opens);

        public bool CanOpen(RelativePath name) => name.Extension == new Extension(".bsa");

        public async Task<Stream?> Open(Stream archive, RelativePath member, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _opens) >= rendezvous && rendezvous > 0) _met.TrySetResult();
            if (rendezvous > 0) await _met.Task;
            return new MemoryStream(content);
        }
    }

    /// <summary>Refuses every back up while <see cref="Fail"/> is set.</summary>
    private sealed class FailingBackups(IFileStore inner) : ForwardingFileStore(inner)
    {
        public bool Fail { get; set; }

        public override Task BackupFiles(IEnumerable<ArchivedFileEntry> backups, bool deduplicate = true, CancellationToken token = default, IProgress<double>? progress = null)
            => Fail ? Task.FromException(new InvalidOperationException("the store refused")) : base.BackupFiles(backups, deduplicate, token, progress);
    }

    /// <summary>Yields the member's own name as its bytes, and counts the archives opened and let go.</summary>
    private sealed class NamedMemberOpener : INestedArchiveOpener
    {
        private int _opened, _disposed;

        public int ArchivesOpened => Volatile.Read(ref _opened);
        public int ArchivesDisposed => Volatile.Read(ref _disposed);

        public bool CanOpen(RelativePath name) => name.Extension == new Extension(".bsa");

        public Task<Stream?> Open(Stream archive, RelativePath member, CancellationToken cancellationToken)
            => Task.FromResult<Stream?>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(member.ToString())));

        public async Task<INestedArchive> OpenArchive(Func<CancellationToken, Task<Stream>> openContainer, CancellationToken cancellationToken)
        {
            await using (await openContainer(cancellationToken)) { }
            Interlocked.Increment(ref _opened);
            return new Archive(this);
        }

        private sealed class Archive(NamedMemberOpener owner) : INestedArchive
        {
            public Task<Stream?> Open(RelativePath member, CancellationToken cancellationToken) => owner.Open(Stream.Null, member, cancellationToken);

            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref owner._disposed);
                return ValueTask.CompletedTask;
            }
        }
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
