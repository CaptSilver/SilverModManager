using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;

namespace SilverModManager.Wabbajack.Tests.Acquire;

/// <summary>
/// A plan that checks many game files reads each of them at most once, a few at a time, and leaves rows behind so
/// the next plan for the same list reads none.
/// </summary>
public class GameFileHashingTests(ITestOutputHelper helper) : AcquireTestBase<GameFileHashingTests>(helper)
{
    private const int FileCount = 12;

    private static RelativePath Name(int i) => RelativePath.FromUnsanitizedInput($"Data/Skyrim - {i}.bsa");

    private static byte[] BytesOf(int i) => Enumerable.Range(0, 20_000 + i).Select(b => (byte)(b * 7 + i)).ToArray();

    protected override Dictionary<RelativePath, byte[]> StubGameFiles
        => Enumerable.Range(0, FileCount).ToDictionary(Name, BytesOf);

    private static ModList ListOfAllFiles()
        => ListWith(Enumerable.Range(0, FileCount)
            .Select(i => Entry(new GameFileSourceState("SkyrimSpecialEdition", Name(i), Hash64(BytesOf(i)), GameVersion), Hash64(BytesOf(i)), BytesOf(i).Length, $"{i}.bsa"))
            .ToArray());

    private ArchiveInventory Inventory() => new(
        ServiceProvider.GetRequiredService<SilverModManager.Wabbajack.Library.ILibraryArchiveLocator>(),
        Fakes.Login(premium: false),
        new FakeMappingCache(),
        Connection,
        FileStore,
        ServiceProvider.GetRequiredService<IFileIdentityResolver>(),
        ServiceProvider.GetRequiredService<IFileHashesService>());

    private GameFileSourceLookup Lookup(Loadout.ReadOnly loadout, Func<AbsolutePath, Stream> open)
        => new(loadout, Connection, FileStore, ServiceProvider.GetRequiredService<IFileIdentityResolver>(), ServiceProvider.GetRequiredService<IFileHashesService>())
        {
            OpenLive = open,
        };

    /// <summary>Counts the files open at once and slows each read, so a loop that runs them together shows it.</summary>
    private sealed class Tracker
    {
        private int _open;
        private int _peak;
        private int _opened;
        public int Peak => _peak;
        public int Opened => _opened;

        public Stream Open(AbsolutePath file)
        {
            Interlocked.Increment(ref _opened);
            var now = Interlocked.Increment(ref _open);
            int seen;
            while ((seen = _peak) < now && Interlocked.CompareExchange(ref _peak, now, seen) != seen) { }
            return new SlowStream(SequentialFileRead.Open(file), () => Interlocked.Decrement(ref _open));
        }
    }

    private sealed class SlowStream(Stream inner, Action onDispose) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) { Thread.Sleep(15); return inner.Read(buffer, offset, count); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(15, cancellationToken);
            return await inner.ReadAsync(buffer, cancellationToken);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { inner.Dispose(); onDispose(); }
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task GameFilesAreReadSeveralAtATimeButNeverMoreThanTheReaderLimit()
    {
        var loadout = await CreateLoadout();
        var tracker = new Tracker();

        var plan = await Inventory().Build(ListOfAllFiles(), Connection.Db, Lookup(loadout, tracker.Open));

        plan.Rows.Should().OnlyContain(row => row.State == ArchiveState.GameFile);
        tracker.Opened.Should().Be(FileCount);
        tracker.Peak.Should().BeInRange(2, HashingLimits.FileReaders, "files are hashed together, bounded by the shared reader limit");
    }

    [Fact]
    public async Task ASecondPlanForTheSameListReadsNothing()
    {
        var loadout = await CreateLoadout();
        var inventory = Inventory();
        await inventory.Build(ListOfAllFiles(), Connection.Db, Lookup(loadout, new Tracker().Open));
        var second = new Tracker();

        var plan = await inventory.Build(ListOfAllFiles(), Connection.Db, Lookup(loadout, second.Open));

        plan.Rows.Should().OnlyContain(row => row.State == ArchiveState.GameFile);
        second.Opened.Should().Be(0, "the first plan left a relation for every file");
    }

    [Fact]
    public async Task AFileChangedBetweenPlansIsTheOnlyOneReadAgain()
    {
        var loadout = await CreateLoadout();
        var inventory = Inventory();
        await inventory.Build(ListOfAllFiles(), Connection.Db, Lookup(loadout, new Tracker().Open));
        var changed = BytesOf(3).Append((byte)9).ToArray();
        await loadout.InstallationInstance.Locations.ToAbsolutePath(new SilverModManager.Sdk.Games.GamePath(SilverModManager.Sdk.Games.LocationId.Game, Name(3))).WriteAllBytesAsync(changed);
        var second = new Tracker();

        var plan = await inventory.Build(ListOfAllFiles(), Connection.Db, Lookup(loadout, second.Open));

        second.Opened.Should().Be(1);
        plan.Rows[3].State.Should().Be(ArchiveState.GameFileVersion);
        plan.Rows.Where((_, i) => i != 3).Should().OnlyContain(row => row.State == ArchiveState.GameFile);
    }

    private sealed class Reports : IProgress<PlanProgress>
    {
        private readonly object _gate = new();
        public List<PlanProgress> All { get; } = [];
        public void Report(PlanProgress value) { lock (_gate) All.Add(value); }
    }

    [Fact]
    public async Task ProgressOfParallelReadsNeverGoesBackwardsAndEndsAtTheTotal()
    {
        var loadout = await CreateLoadout();
        var reports = new Reports();
        var total = Enumerable.Range(0, FileCount).Sum(i => (long)BytesOf(i).Length);

        await Inventory().Build(ListOfAllFiles(), Connection.Db, Lookup(loadout, new Tracker().Open), CancellationToken.None, reports);

        var hashing = reports.All.Where(report => report.Phase == PlanPhase.HashingGameFiles).ToArray();
        hashing.Select(report => report.Done).Should().BeInAscendingOrder();
        hashing.Should().OnlyContain(report => report.Total == total);
        hashing.Last().Done.Should().Be(total);
    }
}
