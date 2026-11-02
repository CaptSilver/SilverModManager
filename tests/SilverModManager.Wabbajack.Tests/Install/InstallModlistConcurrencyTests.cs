using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.IO;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Models;
using NexusMods.Hashing.xxHash3;

namespace SilverModManager.Wabbajack.Tests.Install;

/// <summary>How the install spreads its work: what runs at once, what it may hold, and how often it commits.</summary>
public class InstallModlistConcurrencyTests(ITestOutputHelper helper) : InstallTestBase<InstallModlistConcurrencyTests>(helper)
{
    private static readonly InstallOptions Defaults = new(false, new HashSet<RelativePath>());

    private readonly RendezvousOpener _opener = new(rendezvous: 2);
    private readonly TrackingTransforms _transforms = new();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        var configured = base.AddServices(services);
        // The first opener that can open a path is used, so this one goes ahead of the real one.
        configured.Insert(0, ServiceDescriptor.Singleton<INestedArchiveOpener>(_opener));
        return configured.AddSingleton<IDirectiveTransforms>(provider => _transforms.Over(new DirectiveTransforms(provider.GetRequiredService<TemporaryFileManager>())));
    }

    private static async Task<bool> FinishesWithin(Task task, TimeSpan time) => await Task.WhenAny(task, Task.Delay(time)) == task;

    [Fact]
    public async Task MembersThatNeedAnOpenerAreResolvedAtTheSameTime()
    {
        _opener.Gate = true;
        var archive = await AddZip("a.zip", new() { ["textures.bsa"] = "BSA\0not really"u8.ToArray() });
        var list = new ListBuilder();
        list.Archives.Add(archive);
        foreach (var name in new[] { "a.dds", "b.dds" })
            list.Directives.Add(new FromArchive(P($"mods/A/{name}"), Hash64(RendezvousOpener.Content), RendezvousOpener.Content.Length, new ArchiveHashPath(archive.XxHash64, [P("textures.bsa"), P(name)])));
        var loadout = await CreateLoadout();
        var modlist = await AddModlist(list.ToList(), list.Blobs);

        // Each open waits for the other, so this finishes only if the job resolves both at once.
        var install = Install(loadout, modlist, Defaults);

        (await FinishesWithin(install, TimeSpan.FromSeconds(30))).Should().BeTrue("the second open is never reached while the first one is awaited alone");
        var (_, group) = await install;
        SkipsOf(group).Should().BeEmpty();
        TreeOf(group)["A"].Keys.Should().BeEquivalentTo(["Game/Data/a.dds", "Game/Data/b.dds"]);
    }

    [Fact]
    public async Task PatchesOfOneListAreAppliedAtTheSameTime()
    {
        _transforms.PatchRendezvous = 2;
        var (list, expected) = await PatchList(2);
        var loadout = await CreateLoadout();
        var modlist = await AddModlist(list.ToList(), list.Blobs);

        var install = Install(loadout, modlist, Defaults);

        (await FinishesWithin(install, TimeSpan.FromSeconds(30))).Should().BeTrue("the second patch is never started while the first one is awaited alone");
        var (_, group) = await install;
        SkipsOf(group).Should().BeEmpty();
        foreach (var (path, bytes) in expected)
            (await InstalledBytes(group, "A", $"Game/Data/{path}")).Should().Equal(bytes);
    }

    [Fact]
    public async Task PatchesRunOnFewerWorkersThanOtherDirectivesBecauseEachAllocatesMegabytes()
    {
        var (list, _) = await PatchList(8);
        var loadout = await CreateLoadout();
        var modlist = await AddModlist(list.ToList(), list.Blobs);

        var group = await Run(NewJob(loadout, modlist, Defaults, parallelism: 16));

        SkipsOf(group).Should().BeEmpty();
        TreeOf(group)["A"].Should().HaveCount(8);
        _transforms.MaxConcurrentPatch.Should().BeInRange(1, 2, "an eighth of 16 workers, and Octodiff allocates megabytes for each patch");
    }

    [Fact]
    public async Task ThePathsRemappedFilesAreWrittenWithAreWorkedOutOncePerInstall()
    {
        var list = new ListBuilder();
        for (var i = 0; i < 20; i++) list.Directives.Add(list.Remapped($"mods/A/r{i}.ini", $"path={{--||GAME_PATH_MAGIC_BACK||--}} {i}"));
        var loadout = await CreateLoadout();
        var modlist = await AddModlist(list.ToList(), list.Blobs);
        var job = NewJob(loadout, modlist, Defaults);
        var resolver = (SilverModManager.Games.TestFramework.FakePrefixResolver)job.PrefixResolver;
        var resolves = 0;
        var inner = resolver.Resolution;
        resolver.Resolution = installation =>
        {
            Interlocked.Increment(ref resolves);
            return inner(installation);
        };

        var group = await Run(job);

        SkipsOf(group).Should().BeEmpty();
        TreeOf(group)["A"].Should().HaveCount(20);
        resolves.Should().Be(1, "the game and prefix do not change during an install, and the lookup reads the disk");
    }

    [Fact]
    public async Task WorkInFlightStaysWithinTheByteBudget()
    {
        var (list, _) = await TextureList(6);
        var loadout = await CreateLoadout();
        var modlist = await AddModlist(list.ToList(), list.Blobs);
        // One texture reserves about a megabyte, so a budget of 1.5 MB admits one at a time.
        var job = NewJob(loadout, modlist, Defaults, budget: new ByteBudget(1_500_000));

        var group = await Run(job);

        SkipsOf(group).Should().BeEmpty();
        TreeOf(group)["A"].Should().HaveCount(6);
        _transforms.MaxConcurrentRecompress.Should().Be(1);
    }

    [Fact]
    public async Task TexturesRunTogetherWhenTheBudgetAllowsIt()
    {
        _transforms.RecompressRendezvous = 2;
        var (list, _) = await TextureList(4);
        var loadout = await CreateLoadout();
        var modlist = await AddModlist(list.ToList(), list.Blobs);

        var run = Run(NewJob(loadout, modlist, Defaults, budget: new ByteBudget(1L << 30), parallelism: 4));

        (await FinishesWithin(run, TimeSpan.FromSeconds(30))).Should().BeTrue();
        _transforms.MaxConcurrentRecompress.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task ADirectiveLargerThanTheWholeBudgetStillInstalls()
    {
        var (list, _) = await TextureList(3);
        var loadout = await CreateLoadout();
        var modlist = await AddModlist(list.ToList(), list.Blobs);

        var group = await Run(NewJob(loadout, modlist, Defaults, budget: new ByteBudget(1)));

        SkipsOf(group).Should().BeEmpty();
        TreeOf(group)["A"].Should().HaveCount(3);
    }

    [Fact]
    public async Task GeneratedFilesGoToTheFileStoreWhileTheyAccumulateNotAllAtTheEnd()
    {
        var list = new ListBuilder();
        for (var i = 0; i < 6; i++)
        {
            var bytes = new byte[1_000_000];
            new Random(i).NextBytes(bytes);
            list.Directives.Add(list.Inline($"mods/A/f{i}.bin", bytes));
        }
        var loadout = await CreateLoadout();
        var modlist = await AddModlist(list.ToList(), list.Blobs);
        var counting = new CountingBackups(FileStore);
        var writer = new GeneratedFileWriter(counting, TemporaryFileManager) { StoreByteLimit = 2_500_000 };

        var group = await Run(NewJob(loadout, modlist, Defaults, writer: writer, parallelism: 1));

        SkipsOf(group).Should().BeEmpty();
        TreeOf(group)["A"].Should().HaveCount(6);
        counting.Calls.Should().BeGreaterThanOrEqualTo(2, "2.5 MB of waiting files is the limit, and six files of 1 MB went through");
    }

    [Fact]
    public async Task ManyTinyFilesShareFewCommits()
    {
        var (group, commits) = await InstallInlines(5000, CommitPolicy.Default);

        TreeOf(group)["A"].Should().HaveCount(5000);
        commits.Should().BeLessThanOrEqualTo(5, "five thousand files used to take three commits of their own on top of the install's fixed ones");
    }

    [Fact]
    public async Task ASmallerCommitLimitCommitsMoreOften()
    {
        var (_, few) = await InstallInlines(5000, CommitPolicy.Default);

        var (_, many) = await InstallInlines(5000, new CommitPolicy(500, long.MaxValue, TimeSpan.FromHours(1)));

        many.Should().BeGreaterThanOrEqualTo(few + 8);
    }

    private async Task<(WabbajackModlistLoadoutGroup.ReadOnly Group, int Commits)> InstallInlines(int count, CommitPolicy policy)
    {
        var list = new ListBuilder();
        for (var i = 0; i < count; i++) list.Directives.Add(list.Inline($"mods/A/f{i}.txt", $"file {i}"));
        var loadout = await CreateLoadout();
        var modlist = await AddModlist(list.ToList(), list.Blobs);

        var seen = 0;
        var subscription = Connection.Revisions.Subscribe(_ => Interlocked.Increment(ref seen));
        await Task.Delay(200, TestContext.Current.CancellationToken); // the current revision is delivered to a new subscriber
        var before = Volatile.Read(ref seen);
        var group = await Run(NewJob(loadout, modlist, Defaults, commits: policy));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var total = Volatile.Read(ref seen) - before;
        subscription.Dispose();
        return (group, total);
    }

    private async Task<(ListBuilder List, List<(string Path, byte[] Bytes)> Expected)> PatchList(int count)
    {
        var files = Enumerable.Range(0, count).ToDictionary(i => $"p{i}.bin", i => Enumerable.Range(0, 3000).Select(j => (byte)(i + j)).ToArray());
        var archive = await AddZip("a.zip", files);
        var list = new ListBuilder();
        list.Archives.Add(archive);
        var expected = new List<(string, byte[])>();
        foreach (var (name, basis) in files)
        {
            var result = basis.Select(b => (byte)(b ^ 0x5A)).ToArray();
            list.Directives.Add(list.Patched($"mods/A/{name}", archive, name, basis, result));
            expected.Add((name, result));
        }
        return (list, expected);
    }

    private async Task<(ListBuilder List, int Count)> TextureList(int count)
    {
        var archive = await AddZip("t.zip", Enumerable.Range(0, count).ToDictionary(i => $"t{i}.dds", _ => Bc1Dds(64)));
        var list = new ListBuilder();
        list.Archives.Add(archive);
        for (var i = 0; i < count; i++)
        {
            list.Directives.Add(new TransformedTexture(P($"mods/A/t{i}.dds"), Xx64.From(7), 1,
                new ArchiveHashPath(archive.XxHash64, [P($"t{i}.dds")]), new ImageState(256, 256, 1, "BC7_UNORM")));
        }
        return (list, count);
    }

    /// <summary>Opens any member as the same bytes; with a rendezvous, an open returns only once that many are in progress.</summary>
    private sealed class RendezvousOpener(int rendezvous) : INestedArchiveOpener
    {
        public static readonly byte[] Content = Enumerable.Range(0, 4000).Select(i => (byte)(i * 7)).ToArray();

        private readonly TaskCompletionSource _met = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public bool Gate { get; set; }

        public bool CanOpen(RelativePath name) => name.Extension == new Extension(".bsa");

        public async Task<Stream?> Open(Stream archive, RelativePath member, CancellationToken cancellationToken)
        {
            if (Gate)
            {
                if (Interlocked.Increment(ref _arrived) >= rendezvous) _met.TrySetResult();
                await _met.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
            }
            return new MemoryStream(Content);
        }
    }

    /// <summary>Real transforms, watched: how many textures encode at once, and optionally each waits for others to be in progress.</summary>
    private sealed class TrackingTransforms : IDirectiveTransforms
    {
        private IDirectiveTransforms _inner = null!;
        private int _recompressing, _maxRecompress, _patchArrived, _recompressArrived, _patching, _maxPatch;
        private readonly TaskCompletionSource _patchMet = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _recompressMet = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PatchRendezvous { get; set; }
        public int RecompressRendezvous { get; set; }
        public int MaxConcurrentRecompress => Volatile.Read(ref _maxRecompress);
        public int MaxConcurrentPatch => Volatile.Read(ref _maxPatch);

        public TrackingTransforms Over(IDirectiveTransforms inner)
        {
            _inner = inner;
            return this;
        }

        public async Task Patch(Stream basis, Stream delta, Stream output, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _patching);
            int seen;
            while ((seen = Volatile.Read(ref _maxPatch)) < now && Interlocked.CompareExchange(ref _maxPatch, now, seen) != seen) { }
            try
            {
                if (PatchRendezvous > 0)
                {
                    if (Interlocked.Increment(ref _patchArrived) >= PatchRendezvous) _patchMet.TrySetResult();
                    await _patchMet.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
                }
                else
                {
                    await Task.Delay(30, cancellationToken);
                }
                await _inner.Patch(basis, delta, output, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _patching);
            }
        }

        public async Task Recompress(Stream dds, ImageState target, Stream output, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _recompressing);
            int seen;
            while ((seen = Volatile.Read(ref _maxRecompress)) < now && Interlocked.CompareExchange(ref _maxRecompress, now, seen) != seen) { }
            try
            {
                if (RecompressRendezvous > 0)
                {
                    if (Interlocked.Increment(ref _recompressArrived) >= RecompressRendezvous) _recompressMet.TrySetResult();
                    await _recompressMet.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
                }
                else
                {
                    await Task.Delay(30, cancellationToken); // long enough for a second one to start if the budget lets it
                }
                await _inner.Recompress(dds, target, output, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _recompressing);
            }
        }

        public Task BuildBsa(JsonElement state, JsonElement[] fileStates, Func<RelativePath, Task<Stream>> openInput, Stream output, CancellationToken cancellationToken)
            => _inner.BuildBsa(state, fileStates, openInput, output, cancellationToken);
    }

    /// <summary>Forwards to a store and counts the calls that back files up.</summary>
    private sealed class CountingBackups(IFileStore inner) : IFileStore
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<bool> HaveFile(FileIdentity identity) => inner.HaveFile(identity);

        public Task BackupFiles(IEnumerable<ArchivedFileEntry> backups, bool deduplicate = true, CancellationToken token = default, IProgress<double>? progress = null)
        {
            Interlocked.Increment(ref _calls);
            return inner.BackupFiles(backups, deduplicate, token, progress);
        }

        public Task ExtractFiles(IEnumerable<(FileIdentity Identity, AbsolutePath Dest)> files, CancellationToken token = default, Action<(int Current, int Max)>? progress = null)
            => inner.ExtractFiles(files, token, progress);

        public Task<Dictionary<FileIdentity, byte[]>> ExtractFiles(IEnumerable<FileIdentity> files, CancellationToken token = default) => inner.ExtractFiles(files, token);

        public Task<Stream> GetFileStream(FileIdentity identity, CancellationToken token = default) => inner.GetFileStream(identity, token);

        public Task<byte[]> Load(FileIdentity identity, CancellationToken token = default) => inner.Load(identity, token);

        public SilverModManager.Sdk.Threading.AsyncFriendlyReaderWriterLock.WriteLockDisposable WriteLock() => inner.WriteLock();

        public void ReloadCaches() => inner.ReloadCaches();

        public event Action? ArchivesChanged
        {
            add => inner.ArchivesChanged += value;
            remove => inner.ArchivesChanged -= value;
        }
    }
}

public class InstallModlistCancelTests(ITestOutputHelper helper) : InstallTestBase<InstallModlistCancelTests>(helper)
{
    private static readonly InstallOptions Defaults = new(false, new HashSet<RelativePath>());

    private readonly HangingTextureTransforms _transforms = new();

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The install did not get as far as the test expected");
            await Task.Delay(20);
        }
    }

    private static Task<WabbajackModlistLoadoutGroup.ReadOnly> Wait(SilverModManager.Sdk.Jobs.IJobTask<InstallModlistJob, WabbajackModlistLoadoutGroup.ReadOnly> task)
        => Task.Run(async () => await task).WaitAsync(TimeSpan.FromSeconds(30));

    protected override IServiceCollection AddServices(IServiceCollection services)
        => base.AddServices(services).AddSingleton<IDirectiveTransforms>(_transforms);

    [Fact]
    public async Task CancellingWhileATextureEncodesKeepsTheCommittedBatchesAndNothingOfTheOpenOne()
    {
        var archive = await AddZip("t.zip", new() { ["t.dds"] = Bc1Dds(8) });
        var list = new ListBuilder();
        list.Archives.Add(archive);
        for (var i = 0; i < 5; i++) list.Directives.Add(list.Inline($"mods/A/f{i}.txt", $"file {i}"));
        // Sorts after the inline files; its encode never ends until the install is cancelled.
        list.Directives.Add(new TransformedTexture(P("mods/A/z.dds"), Xx64.From(1), 1, new ArchiveHashPath(archive.XxHash64, [P("t.dds")]), new ImageState(4, 4, 1, "BC7_UNORM")));
        list.Directives.Add(list.Inline("mods/A/after.txt", "after"));
        var loadout = await CreateLoadout();
        var modlist = await AddModlist(list.ToList(), list.Blobs);

        var task = InstallModlistJob.Create(ServiceProvider, loadout.LoadoutId, modlist, Defaults);
        // With the default commit limits everything written so far is still in the one open batch.
        await _transforms.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        task.Job.AsContext().Cancel();

        var failure = await Record.ExceptionAsync(async () => await Wait(task));

        failure.Should().BeAssignableTo<OperationCanceledException>();
        var group = WabbajackModlistLoadoutGroup.FindByModlist(Connection.Db, modlist).Single();
        TreeOf(group).Values.SelectMany(files => files.Keys).Should().BeEmpty("one batch was open, and an unfinished batch is never committed");
        SkipsOf(group).Should().BeEmpty();
        group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().IsDisabled.Should().BeTrue("a stopped install is not enabled");
    }

    [Fact]
    public async Task InstallingAgainAfterACancelledRunFinishesTheList()
    {
        var archive = await AddZip("t.zip", new() { ["t.dds"] = Bc1Dds(8) });
        var list = new ListBuilder();
        list.Archives.Add(archive);
        for (var i = 0; i < 5; i++) list.Directives.Add(list.Inline($"mods/A/f{i}.txt", $"file {i}"));
        list.Directives.Add(new TransformedTexture(P("mods/A/z.dds"), Xx64.From(1), 1, new ArchiveHashPath(archive.XxHash64, [P("t.dds")]), new ImageState(4, 4, 1, "BC7_UNORM")));
        var loadout = await CreateLoadout();
        var modlist = await AddModlist(list.ToList(), list.Blobs);
        var first = NewJob(loadout, modlist, Defaults, commits: new CommitPolicy(2, long.MaxValue, TimeSpan.FromHours(1)));
        var running = ServiceProvider.GetRequiredService<SilverModManager.Sdk.Jobs.IJobMonitor>()
            .Begin<InstallModlistJob, WabbajackModlistLoadoutGroup.ReadOnly>(first);
        await _transforms.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        // The texture starts while earlier files are still being recorded; wait for the two commits before stopping.
        await WaitUntil(() => WabbajackModlistLoadoutGroup.FindByModlist(Connection.Db, modlist).FirstOrDefault() is { } found && TreeOf(found).GetValueOrDefault("A")?.Count == 4);
        running.Job.AsContext().Cancel();
        await Record.ExceptionAsync(async () => await Wait(running));
        var group = WabbajackModlistLoadoutGroup.FindByModlist(Connection.Db, modlist).Single();
        TreeOf(group)["A"].Should().HaveCount(4, "two batches of two were committed before the texture; the fifth file was in the open one");

        _transforms.Hang = false;
        var (_, resumed) = await Install(loadout, modlist, Defaults);

        TreeOf(resumed)["A"].Keys.Should().BeEquivalentTo(["Game/Data/f0.txt", "Game/Data/f1.txt", "Game/Data/f2.txt", "Game/Data/f3.txt", "Game/Data/f4.txt", "Game/Data/z.dds"]);
        SkipsOf(resumed).Should().BeEmpty();
    }

    /// <summary>A texture encode that starts, says so, and ends only when cancelled (until told not to hang).</summary>
    private sealed class HangingTextureTransforms : IDirectiveTransforms
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Hang { get; set; } = true;

        public Task Patch(Stream basis, Stream delta, Stream output, CancellationToken cancellationToken) => throw new InvalidOperationException("no patch here");

        public async Task Recompress(Stream dds, ImageState target, Stream output, CancellationToken cancellationToken)
        {
            if (Hang)
            {
                Started.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            await dds.CopyToAsync(output, cancellationToken);
        }

        public Task BuildBsa(JsonElement state, JsonElement[] fileStates, Func<RelativePath, Task<Stream>> openInput, Stream output, CancellationToken cancellationToken)
            => throw new InvalidOperationException("no archive here");
    }
}
