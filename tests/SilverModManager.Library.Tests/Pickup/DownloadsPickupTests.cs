using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using R3;
using SilverModManager.Library.Pickup;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Native;
using SilverModManager.Wabbajack.Tests.Acquire;
using Xunit;

namespace SilverModManager.Library.Tests.Pickup;

public class DownloadsPickupTests : IDisposable
{
    private static readonly byte[] Wanted = "the bytes the list expects"u8.ToArray();
    private static readonly byte[] SameLength = "the bytes the list EXPECTS"u8.ToArray();

    private readonly AbsolutePath _root = NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath(Directory.CreateTempSubdirectory("pickup-").FullName);
    private readonly FakeSettings _settings = new();
    private readonly ExpectedDownloads _expected = new();
    private readonly List<IDisposable> _disposables = [];

    public DownloadsPickupTests()
    {
        _settings.DownloadsFolder = DownloadsFolder.ToString();
        DownloadsFolder.CreateDirectory();
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
        Directory.Delete(_root.ToString(), recursive: true);
    }

    private AbsolutePath DownloadsFolder => _root.Combine("Downloads");

    private sealed class FakeSettings : IPickupSettings
    {
        private readonly Subject<Unit> _changes = new();
        private bool _on;
        private string _folder = "";

        public bool PickUpFromDownloads
        {
            get => _on;
            set
            {
                _on = value;
                _changes.OnNext(Unit.Default);
            }
        }

        public string DownloadsFolder
        {
            get => _folder;
            set
            {
                _folder = value;
                _changes.OnNext(Unit.Default);
            }
        }

        public Observable<Unit> Changes => _changes;
    }

    private sealed class RecordingSink : IPickupSink
    {
        public ConcurrentQueue<string> Accepted { get; } = new();
        public Func<AbsolutePath, Func<string, string, int>, Task>? OnAccept { get; set; }

        public async Task Accept(AbsolutePath file, Func<string, string, int> link, CancellationToken cancellationToken)
        {
            if (OnAccept is not null) await OnAccept(file, link);
            Accepted.Enqueue(file.FileName);
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages;

        public ILogger CreateLogger(string categoryName) => new Capture(_messages);

        public void Dispose() { }

        private sealed class Capture(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => messages.Enqueue(formatter(state, exception));
        }
    }

    private static FileIdentity Hash64(byte[] bytes) => FileIdentity.XxHash64(Xx64Hasher.Hash(bytes));

    private IDisposable Expect(RecordingSink sink, string name, byte[] bytes, bool withHash = true)
    {
        var registration = _expected.Expect(new ExpectedDownload(name, bytes.Length, withHash ? Hash64(bytes) : null, sink));
        _disposables.Add(registration);
        return registration;
    }

    private (DownloadsPickup Pickup, RecordingFileSystem Log) NewPickup(TimeSpan? debounce = null, ILogger<DownloadsPickup>? logger = null)
    {
        var (fileSystem, log) = RecordingFileSystem.Wrap(NexusMods.Paths.FileSystem.Shared);
        var pickup = new DownloadsPickup(_settings, _expected, fileSystem, logger ?? NullLogger<DownloadsPickup>.Instance)
        {
            DebounceInterval = debounce ?? TimeSpan.FromMinutes(1),
        };
        pickup.IsPlainFile = file =>
        {
            log.Note("IsPlainFile", file);
            return PlainFiles.IsPlainFile(file);
        };
        _disposables.Add(pickup);
        return (pickup, log);
    }

    private static async Task Drop(AbsolutePath folder, string name, byte[] content, bool settled = true)
    {
        var file = folder.Combine(name);
        await file.WriteAllBytesAsync(content);
        if (settled) File.SetLastWriteTimeUtc(file.ToString(), DateTime.UtcNow - TimeSpan.FromMinutes(5));
    }

    private static IEnumerable<string> TouchedOutside(RecordingFileSystem log, AbsolutePath folder)
        => log.Paths.Where(p => p != folder.ToString());

    private static int Reads(RecordingFileSystem log, string name)
        => log.Calls.Count(c => c.Method is "ReadFile" or "OpenFile" or "Open" && c.Path.EndsWith("/" + name));

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(50, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WithThePickupOff_ScanDoesNotLookAtTheFolder()
    {
        var sink = new RecordingSink();
        _settings.PickUpFromDownloads = false;
        Expect(sink, "wanted.txt", Wanted);
        await Drop(DownloadsFolder, "wanted.txt", Wanted);
        var (pickup, log) = NewPickup();

        await pickup.Scan(CancellationToken.None);

        log.Calls.Should().BeEmpty("a folder the user did not hand over is not read at all");
        sink.Accepted.Should().BeEmpty();
    }

    [Fact]
    public async Task AFileWithTheExpectedNameSizeAndBytes_GoesToItsSinkAndStaysInDownloads()
    {
        var sink = new RecordingSink();
        _settings.PickUpFromDownloads = true;
        Expect(sink, "wanted.txt", Wanted);
        await Drop(DownloadsFolder, "wanted.txt", Wanted);
        var (pickup, _) = NewPickup();

        await pickup.Scan(CancellationToken.None);

        sink.Accepted.Should().Equal("wanted.txt");
        DownloadsFolder.Combine("wanted.txt").FileExists.Should().BeTrue("nothing in Downloads is deleted or moved");
    }

    [Fact]
    public async Task AnExpectationWithoutAHashMatchesOnNameAndSizeAlone()
    {
        var sink = new RecordingSink();
        _settings.PickUpFromDownloads = true;
        Expect(sink, "wanted.txt", Wanted, withHash: false);
        await Drop(DownloadsFolder, "wanted.txt", SameLength);
        var (pickup, log) = NewPickup();

        await pickup.Scan(CancellationToken.None);

        sink.Accepted.Should().Equal(["wanted.txt"], "the sink checks the bytes against the source's own digest");
        Reads(log, "wanted.txt").Should().Be(0, "the pickup has no hash to compare against, so it reads nothing");
    }

    [Fact]
    public async Task AMatchedFileGoesToItsOwnSink()
    {
        var first = new RecordingSink();
        var second = new RecordingSink();
        _settings.PickUpFromDownloads = true;
        Expect(first, "one.txt", Wanted);
        Expect(second, "two.txt", SameLength);
        await Drop(DownloadsFolder, "two.txt", SameLength);
        var (pickup, _) = NewPickup();

        await pickup.Scan(CancellationToken.None);

        first.Accepted.Should().BeEmpty();
        second.Accepted.Should().Equal("two.txt");
    }

    [Fact]
    public async Task AFolderOfUnrelatedFiles_IsNeverStatedOpenedOrHashed()
    {
        var sink = new RecordingSink();
        _settings.PickUpFromDownloads = true;
        Expect(sink, "wanted.txt", Wanted);
        for (var i = 0; i < 200; i++) await Drop(DownloadsFolder, $"other-{i}.bin", Wanted);
        await Drop(DownloadsFolder, "wanted.txt", Wanted);
        var (pickup, log) = NewPickup();

        await pickup.Scan(CancellationToken.None);

        TouchedOutside(log, DownloadsFolder).Should().OnlyContain(p => p.EndsWith("/wanted.txt"));
        sink.Accepted.Should().Equal("wanted.txt");
    }

    [Fact]
    public async Task RightBytesUnderAnotherName_AreNotTouched()
    {
        var sink = new RecordingSink();
        _settings.PickUpFromDownloads = true;
        Expect(sink, "wanted.txt", Wanted);
        await Drop(DownloadsFolder, "renamed-by-the-browser.txt", Wanted);
        var (pickup, log) = NewPickup();

        await pickup.Scan(CancellationToken.None);

        TouchedOutside(log, DownloadsFolder).Should().BeEmpty();
        sink.Accepted.Should().BeEmpty();
    }

    [Fact]
    public async Task AFileOfTheWrongSize_IsLeftAloneAndNeverOpened()
    {
        var sink = new RecordingSink();
        _settings.PickUpFromDownloads = true;
        Expect(sink, "wanted.txt", Wanted);
        await Drop(DownloadsFolder, "wanted.txt", Wanted.Take(5).ToArray());
        var (pickup, log) = NewPickup();

        await pickup.Scan(CancellationToken.None);

        log.Calls.Where(c => c.Method is "ReadFile" or "OpenFile" or "Open").Should().BeEmpty();
        sink.Accepted.Should().BeEmpty();
    }

    [Fact]
    public async Task TheRightSizeWithWrongBytes_IsNotAcceptedAndHashedOnceAcrossScans()
    {
        var sink = new RecordingSink();
        _settings.PickUpFromDownloads = true;
        Expect(sink, "wanted.txt", Wanted);
        await Drop(DownloadsFolder, "wanted.txt", SameLength);
        var (pickup, log) = NewPickup();

        await pickup.Scan(CancellationToken.None);
        await pickup.Scan(CancellationToken.None);

        sink.Accepted.Should().BeEmpty();
        Reads(log, "wanted.txt").Should().Be(1);
    }

    [Fact]
    public async Task AChangedFileIsHashedAgainAfterAMismatch()
    {
        var sink = new RecordingSink();
        _settings.PickUpFromDownloads = true;
        Expect(sink, "wanted.txt", Wanted);
        await Drop(DownloadsFolder, "wanted.txt", SameLength);
        var (pickup, _) = NewPickup();
        await pickup.Scan(CancellationToken.None);

        await Drop(DownloadsFolder, "wanted.txt", Wanted);
        await pickup.Scan(CancellationToken.None);

        sink.Accepted.Should().Equal("wanted.txt");
    }

    [Fact]
    public async Task AFileStillBeingWritten_WaitsForTheScheduledRescan()
    {
        var sink = new RecordingSink();
        _settings.PickUpFromDownloads = true;
        Expect(sink, "wanted.txt", Wanted);
        await Drop(DownloadsFolder, "wanted.txt", Wanted, settled: false);
        var (pickup, _) = NewPickup(TimeSpan.FromHours(1));

        await pickup.Scan(CancellationToken.None);
        sink.Accepted.Should().BeEmpty("a file written a moment ago may still be growing");

        pickup.Dispose();
        var (quick, _) = NewPickup(TimeSpan.FromMilliseconds(300));
        await quick.Scan(CancellationToken.None);

        await Until(() => !sink.Accepted.IsEmpty);
        sink.Accepted.Should().Equal("wanted.txt");
    }

    [Fact]
    public async Task BrowserPartialFilesAreIgnoredUntilTheFinalNameAppears()
    {
        var sink = new RecordingSink();
        _settings.PickUpFromDownloads = true;
        Expect(sink, "wanted.txt", Wanted);
        await Drop(DownloadsFolder, "wanted.txt.part", Wanted);
        await Drop(DownloadsFolder, "wanted.txt.crdownload", Wanted);
        await Drop(DownloadsFolder, "wanted.txt", []);
        var (pickup, log) = NewPickup();

        await pickup.Scan(CancellationToken.None);

        sink.Accepted.Should().BeEmpty();
        TouchedOutside(log, DownloadsFolder).Should().OnlyContain(p => p.EndsWith("/wanted.txt"), "the placeholder is only measured, never read");
        log.Calls.Where(c => c.Method is "ReadFile" or "OpenFile" or "Open").Should().BeEmpty();
    }

    [Fact]
    public async Task ASymlinkWithTheExpectedName_IsNotFollowed()
    {
        var sink = new RecordingSink();
        _settings.PickUpFromDownloads = true;
        Expect(sink, "wanted.txt", Wanted);
        var elsewhere = _root.Combine("elsewhere");
        elsewhere.CreateDirectory();
        await Drop(elsewhere, "real.bin", Wanted);
        File.CreateSymbolicLink(DownloadsFolder.Combine("wanted.txt").ToString(), elsewhere.Combine("real.bin").ToString());
        var (pickup, _) = NewPickup();

        await pickup.Scan(CancellationToken.None);

        sink.Accepted.Should().BeEmpty();
    }

    [Fact]
    public async Task TwoExpectationsSharingAFileName_GoToTheOneWhoseHashMatches()
    {
        var first = new RecordingSink();
        var second = new RecordingSink();
        _settings.PickUpFromDownloads = true;
        Expect(first, "wanted.txt", Wanted);
        Expect(second, "wanted.txt", SameLength);
        await Drop(DownloadsFolder, "wanted.txt", SameLength);
        var (pickup, _) = NewPickup();

        await pickup.Scan(CancellationToken.None);

        first.Accepted.Should().BeEmpty();
        second.Accepted.Should().Equal("wanted.txt");
    }

    [Fact]
    public async Task TheSinkIsHandedTheLinkFunctionToTryBeforeCopying()
    {
        var sink = new RecordingSink();
        var attempts = 0;
        sink.OnAccept = (_, link) =>
        {
            attempts += link("a", "b") == LibcLinks.EXDEV ? 1 : 0;
            return Task.CompletedTask;
        };
        _settings.PickUpFromDownloads = true;
        Expect(sink, "wanted.txt", Wanted);
        await Drop(DownloadsFolder, "wanted.txt", Wanted);
        var (pickup, _) = NewPickup();
        pickup.Link = (_, _) => LibcLinks.EXDEV;

        await pickup.Scan(CancellationToken.None);

        attempts.Should().Be(1);
        sink.Accepted.Should().Equal("wanted.txt");
    }

    [Fact]
    public async Task AFolderThatDoesNotExist_IsQuietlySkipped()
    {
        var sink = new RecordingSink();
        _settings.PickUpFromDownloads = true;
        _settings.DownloadsFolder = _root.Combine("not-there").ToString();
        Expect(sink, "wanted.txt", Wanted);
        var (pickup, _) = NewPickup();

        await pickup.Scan(CancellationToken.None);

        sink.Accepted.Should().BeEmpty();
    }

    [Fact]
    public async Task AnExpectationThatEnded_ExpectsNothing()
    {
        var sink = new RecordingSink();
        _settings.PickUpFromDownloads = true;
        var registration = Expect(sink, "wanted.txt", Wanted);
        registration.Dispose();
        await Drop(DownloadsFolder, "wanted.txt", Wanted);
        var (pickup, log) = NewPickup();

        await pickup.Scan(CancellationToken.None);

        TouchedOutside(log, DownloadsFolder).Should().BeEmpty("a download nobody waits for is not looked at");
        sink.Accepted.Should().BeEmpty();
    }

    [Fact]
    public async Task AFolderOfUnrelatedFiles_NeverGetsALinkCheck()
    {
        var sink = new RecordingSink();
        _settings.PickUpFromDownloads = true;
        Expect(sink, "wanted.txt", Wanted);
        for (var i = 0; i < 50; i++) await Drop(DownloadsFolder, $"other-{i}.bin", Wanted);
        var (pickup, log) = NewPickup();

        await pickup.Scan(CancellationToken.None);

        log.Calls.Where(c => c.Method == "IsPlainFile").Select(c => c.Path).Should().BeEmpty("no file in the folder has the expected name");
    }

    [Fact]
    public async Task AFailedHandOver_IsReportedOnceAndTheSameBytesAreNotHashedAgain()
    {
        var sink = new RecordingSink();
        var attempts = 0;
        sink.OnAccept = (_, _) =>
        {
            attempts++;
            throw new IOException("No space left on device");
        };
        _settings.PickUpFromDownloads = true;
        Expect(sink, "wanted.txt", Wanted);
        await Drop(DownloadsFolder, "wanted.txt", Wanted);
        var (pickup, log) = NewPickup();

        await pickup.Scan(CancellationToken.None);
        await pickup.Scan(CancellationToken.None);

        attempts.Should().Be(1, "a copy that failed is not retried until the file changes");
        Reads(log, "wanted.txt").Should().Be(1);
        sink.Accepted.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedHandOver_DoesNotScheduleARescan()
    {
        var sink = new RecordingSink();
        var attempts = 0;
        sink.OnAccept = (_, _) =>
        {
            attempts++;
            throw new IOException("No space left on device");
        };
        _settings.PickUpFromDownloads = true;
        Expect(sink, "wanted.txt", Wanted);
        await Drop(DownloadsFolder, "wanted.txt", Wanted);
        var (pickup, _) = NewPickup(TimeSpan.FromMilliseconds(100));

        await pickup.Scan(CancellationToken.None);
        await Task.Delay(1000, TestContext.Current.CancellationToken);

        attempts.Should().Be(1);
    }

    [Fact]
    public async Task AFailedHandOver_IsRetriedOnceTheMismatchMemoryIsForgotten()
    {
        var sink = new RecordingSink();
        var fail = true;
        sink.OnAccept = (_, _) => fail ? throw new IOException("No space left on device") : Task.CompletedTask;
        _settings.PickUpFromDownloads = true;
        Expect(sink, "wanted.txt", Wanted);
        await Drop(DownloadsFolder, "wanted.txt", Wanted);
        var (pickup, _) = NewPickup();
        await pickup.Scan(CancellationToken.None);

        fail = false;
        pickup.ForgetMismatches();
        await pickup.Scan(CancellationToken.None);

        sink.Accepted.Should().Equal("wanted.txt");
    }

    [Fact]
    public async Task AnUnreadableExpectedFile_DoesNotStopTheOthersFromBeingHandedOver()
    {
        var sink = new RecordingSink();
        _settings.PickUpFromDownloads = true;
        Expect(sink, "locked.txt", Wanted);
        Expect(sink, "fine.txt", SameLength);
        await Drop(DownloadsFolder, "locked.txt", Wanted);
        await Drop(DownloadsFolder, "fine.txt", SameLength);
        var locked = DownloadsFolder.Combine("locked.txt").ToString();
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var (pickup, _) = NewPickup();

            await pickup.Scan(CancellationToken.None);

            sink.Accepted.Should().Equal("fine.txt");
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task AnUnreadableExpectedFile_IsNotOpenedAgainUntilItChanges()
    {
        var sink = new RecordingSink();
        _settings.PickUpFromDownloads = true;
        Expect(sink, "locked.txt", Wanted);
        await Drop(DownloadsFolder, "locked.txt", Wanted);
        var locked = DownloadsFolder.Combine("locked.txt").ToString();
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var (pickup, log) = NewPickup();

            await pickup.Scan(CancellationToken.None);
            await pickup.Scan(CancellationToken.None);

            Reads(log, "locked.txt").Should().Be(1);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task ThePickupWatchesOnlyOncePickingUpIsOnAndADownloadIsExpected()
    {
        var sink = new RecordingSink();
        _settings.PickUpFromDownloads = true;
        var (pickup, _) = NewPickup();
        pickup.IsWatching.Should().BeFalse("nothing follows the expectations before Follow");

        pickup.Follow();
        pickup.IsWatching.Should().BeFalse("nothing is expected yet");
        Expect(sink, "wanted.txt", Wanted);

        pickup.IsWatching.Should().BeTrue();
        await Task.CompletedTask;
    }

    [Fact]
    public void FollowingTwice_StillWatchesOnce()
    {
        _settings.PickUpFromDownloads = true;
        Expect(new RecordingSink(), "wanted.txt", Wanted);
        var (pickup, _) = NewPickup();

        pickup.Follow();
        pickup.Follow();

        pickup.IsWatching.Should().BeTrue();
    }

    [Fact]
    public async Task TheWatchEnds_WhenTheLastExpectationEnds()
    {
        _settings.PickUpFromDownloads = true;
        var registration = Expect(new RecordingSink(), "wanted.txt", Wanted);
        var (pickup, _) = NewPickup();
        pickup.Follow();
        pickup.IsWatching.Should().BeTrue();

        registration.Dispose();

        await Until(() => !pickup.IsWatching);
        pickup.IsWatching.Should().BeFalse();
    }

    [Fact]
    public async Task SwitchingThePickupOff_EndsTheWatchWhileDownloadsAreStillExpected()
    {
        _settings.PickUpFromDownloads = true;
        Expect(new RecordingSink(), "wanted.txt", Wanted);
        var (pickup, _) = NewPickup();
        pickup.Follow();
        pickup.IsWatching.Should().BeTrue();

        _settings.PickUpFromDownloads = false;

        await Until(() => !pickup.IsWatching);
        pickup.IsWatching.Should().BeFalse();

        _settings.PickUpFromDownloads = true;
        await Until(() => pickup.IsWatching);
        pickup.IsWatching.Should().BeTrue("switching it back on starts the watch again");
    }

    [Fact]
    public async Task AMissingFolder_IsNotWatchedAndIsSaidOnce()
    {
        _settings.PickUpFromDownloads = true;
        _settings.DownloadsFolder = DownloadsFolder.Combine("not-there").ToString();
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var (pickup, _) = NewPickup(logger: factory.CreateLogger<DownloadsPickup>());

        pickup.Follow();
        Expect(new RecordingSink(), "wanted.txt", Wanted);
        await pickup.Scan(CancellationToken.None);

        pickup.IsWatching.Should().BeFalse();
        logs.Messages.Where(m => m.Contains("does not exist")).Should().ContainSingle();
    }

    [Fact]
    public async Task ChoosingAnotherFolderWhileWatching_MovesTheWatchToIt()
    {
        var sink = new RecordingSink();
        _settings.PickUpFromDownloads = true;
        var folderB = _root.Combine("browser-downloads");
        folderB.CreateDirectory();
        var registration = Expect(sink, "wanted.txt", Wanted);
        var (pickup, _) = NewPickup(TimeSpan.FromMilliseconds(100));
        pickup.Follow();
        pickup.IsWatching.Should().BeTrue();
        pickup.WatchedFolder.Should().Be(DownloadsFolder);

        _settings.DownloadsFolder = folderB.ToString();
        await Drop(folderB, "wanted.txt", Wanted);

        await Until(() => !sink.Accepted.IsEmpty);
        sink.Accepted.Should().Equal("wanted.txt");
        pickup.WatchedFolder.Should().Be(folderB, "the watch follows the folder the user chose");
        registration.Dispose();
    }

    [Fact]
    public async Task ChoosingAnotherFolderWhileWatching_ReportsTheNewFolderAsWatched()
    {
        _settings.PickUpFromDownloads = true;
        var folderB = _root.Combine("browser-downloads");
        folderB.CreateDirectory();
        Expect(new RecordingSink(), "wanted.txt", Wanted);
        var (pickup, _) = NewPickup();
        pickup.Follow();

        _settings.DownloadsFolder = folderB.ToString();

        await Until(() => pickup.IsWatching && pickup.WatchedFolder == folderB);
        pickup.IsWatching.Should().BeTrue();
        pickup.WatchedFolder.Should().Be(pickup.Folder);
        pickup.WatchedFolder.Should().Be(folderB);
    }

    [Fact]
    public void ChoosingAMissingFolderWhileWatching_EndsTheWatch()
    {
        _settings.PickUpFromDownloads = true;
        Expect(new RecordingSink(), "wanted.txt", Wanted);
        var (pickup, _) = NewPickup();
        pickup.Follow();
        pickup.IsWatching.Should().BeTrue();

        _settings.DownloadsFolder = _root.Combine("gone").ToString();

        pickup.IsWatching.Should().BeFalse("a folder that does not exist cannot be watched");
        pickup.WatchedFolder.Should().BeNull();
    }
}
