using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Native;
using SilverModManager.Sdk.Settings;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Tests.Install;

namespace SilverModManager.Wabbajack.Tests.Acquire;

public class DownloadsPickupTests(ITestOutputHelper helper) : AcquireTestBase<DownloadsPickupTests>(helper)
{
    private static readonly byte[] Wanted = "the bytes the list expects"u8.ToArray();
    private static readonly byte[] SameLength = "the bytes the list EXPECTS"u8.ToArray();

    private IArchiveAcquirer Acquirer => ServiceProvider.GetRequiredService<IArchiveAcquirer>();
    private ISettingsManager Settings => ServiceProvider.GetRequiredService<ISettingsManager>();
    private AbsolutePath InboxFolder => ServiceProvider.GetRequiredService<WabbajackPaths>().Inbox;

    private AbsolutePath DownloadsFolder
    {
        get
        {
            var folder = NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath(Settings.Get<WabbajackSettings>().DownloadsFolder);
            folder.CreateDirectory();
            return folder;
        }
    }

    private void PickupIs(bool on) => Settings.Update<WabbajackSettings>(s => s with { PickUpFromDownloads = on });

    private (DownloadsPickup Pickup, RecordingFileSystem Log) NewPickup(TimeSpan? debounce = null, ILogger<DownloadsPickup>? logger = null)
    {
        var (fileSystem, log) = RecordingFileSystem.Wrap(NexusMods.Paths.FileSystem.Shared);
        var pickup = new DownloadsPickup(
            Settings,
            ServiceProvider.GetRequiredService<WabbajackPaths>(),
            Acquirer,
            ServiceProvider.GetRequiredService<ArchiveInbox>(),
            fileSystem,
            logger ?? NullLogger<DownloadsPickup>.Instance)
        {
            DebounceInterval = debounce ?? TimeSpan.FromMinutes(1),
        };
        pickup.IsPlainFile = file =>
        {
            log.Note("IsPlainFile", file);
            return ArchiveInbox.IsPlainFile(file);
        };
        return (pickup, log);
    }

    private async Task RunWithManualRows(params (string Name, byte[] Content)[] archives)
    {
        var rows = archives.Select(a => new ArchivePlanRow(
            Entry(new ManualState(new Uri("https://example.test/m"), "get it"), Hash64(a.Content), a.Content.Length, a.Name),
            ArchiveState.Manual, "get it", new Uri("https://example.test/m"), null)).ToArray();
        await Acquirer.Run(new ArchivePlan(rows), null, CancellationToken.None);
    }

    private static async Task Drop(AbsolutePath folder, string name, byte[] content, bool settled = true)
    {
        var file = folder.Combine(name);
        await file.WriteAllBytesAsync(content);
        if (settled) File.SetLastWriteTimeUtc(file.ToString(), DateTime.UtcNow - TimeSpan.FromMinutes(5));
    }

    private static IEnumerable<string> TouchedOutside(RecordingFileSystem log, AbsolutePath folder)
        => log.Paths.Where(p => p != folder.ToString());

    [Fact]
    public void ThePickupIsOffAndFollowsTheXdgFolderByDefault()
    {
        var settings = WabbajackSettings.CreateDefault(ServiceProvider);

        settings.PickUpFromDownloads.Should().BeFalse();
        settings.DownloadsFolder.Should().BeEmpty();
    }

    [Fact]
    public async Task WithThePickupOff_ScanDoesNotLookAtTheFolder()
    {
        using var page = Acquirer.Attach();
        PickupIs(false); // the settings store outlives a single test
        await RunWithManualRows(("wanted.txt", Wanted));
        await Drop(DownloadsFolder, "wanted.txt", Wanted);
        var (pickup, log) = NewPickup();

        await pickup.Scan(CancellationToken.None);

        log.Calls.Should().BeEmpty("a folder the user did not hand over is not read at all");
        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Manual);
    }

    [Fact]
    public async Task AFileWithTheExpectedNameSizeAndBytes_IsCreditedAndStaysInDownloads()
    {
        using var page = Acquirer.Attach();
        PickupIs(true);
        await RunWithManualRows(("wanted.txt", Wanted));
        await Drop(DownloadsFolder, "wanted.txt", Wanted);
        var (pickup, _) = NewPickup();

        await pickup.Scan(CancellationToken.None);

        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Have);
        DownloadsFolder.Combine("wanted.txt").FileExists.Should().BeTrue("nothing in Downloads is deleted or moved");
        InboxFolder.EnumerateFiles().Should().BeEmpty("the inbox scan removes the staged name once it is in the library");
    }

    [Fact]
    public async Task AFolderOfUnrelatedFiles_IsNeverStatedOpenedOrHashed()
    {
        using var page = Acquirer.Attach();
        PickupIs(true);
        await RunWithManualRows(("wanted.txt", Wanted));
        for (var i = 0; i < 200; i++) await Drop(DownloadsFolder, $"other-{i}.bin", Wanted);
        await Drop(DownloadsFolder, "wanted.txt", Wanted);
        var (pickup, log) = NewPickup();

        await pickup.Scan(CancellationToken.None);

        TouchedOutside(log, DownloadsFolder).Should().OnlyContain(p => p.EndsWith("/wanted.txt"));
        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Have);
    }

    [Fact]
    public async Task RightBytesUnderAnotherName_AreNotTouched()
    {
        using var page = Acquirer.Attach();
        PickupIs(true);
        await RunWithManualRows(("wanted.txt", Wanted));
        await Drop(DownloadsFolder, "renamed-by-the-browser.txt", Wanted);
        var (pickup, log) = NewPickup();

        await pickup.Scan(CancellationToken.None);

        TouchedOutside(log, DownloadsFolder).Should().BeEmpty();
        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Manual);
    }

    [Fact]
    public async Task AFileOfTheWrongSize_IsLeftAloneAndNeverOpened()
    {
        using var page = Acquirer.Attach();
        PickupIs(true);
        await RunWithManualRows(("wanted.txt", Wanted));
        await Drop(DownloadsFolder, "wanted.txt", Wanted.Take(5).ToArray());
        var (pickup, log) = NewPickup();

        await pickup.Scan(CancellationToken.None);

        log.Calls.Where(c => c.Method is "ReadFile" or "OpenFile" or "Open").Should().BeEmpty();
        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Manual);
    }

    [Fact]
    public async Task TheRightSizeWithWrongBytes_IsNotStagedAndHashedOnceAcrossScans()
    {
        using var page = Acquirer.Attach();
        PickupIs(true);
        await RunWithManualRows(("wanted.txt", Wanted));
        await Drop(DownloadsFolder, "wanted.txt", SameLength);
        var (pickup, log) = NewPickup();

        await pickup.Scan(CancellationToken.None);
        await pickup.Scan(CancellationToken.None);

        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Manual);
        InboxFolder.EnumerateFiles().Should().BeEmpty();
        log.Calls.Count(c => c.Method is "ReadFile" or "OpenFile" or "Open" && c.Path.EndsWith("/wanted.txt")).Should().Be(1);
    }

    [Fact]
    public async Task AChangedFileIsHashedAgainAfterAMismatch()
    {
        using var page = Acquirer.Attach();
        PickupIs(true);
        await RunWithManualRows(("wanted.txt", Wanted));
        await Drop(DownloadsFolder, "wanted.txt", SameLength);
        var (pickup, _) = NewPickup();
        await pickup.Scan(CancellationToken.None);

        await Drop(DownloadsFolder, "wanted.txt", Wanted);
        await pickup.Scan(CancellationToken.None);

        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Have);
    }

    [Fact]
    public async Task AFileStillBeingWritten_WaitsForTheScheduledRescan()
    {
        using var page = Acquirer.Attach();
        PickupIs(true);
        await RunWithManualRows(("wanted.txt", Wanted));
        await Drop(DownloadsFolder, "wanted.txt", Wanted, settled: false);
        var (pickup, _) = NewPickup(TimeSpan.FromHours(1));

        await pickup.Scan(CancellationToken.None);
        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Manual, "a file written a moment ago may still be growing");

        pickup.Dispose();
        var (quick, _) = NewPickup(TimeSpan.FromMilliseconds(300));
        await quick.Scan(CancellationToken.None);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (Acquirer.Plan.Rows[0].State != ArchiveState.Have && DateTime.UtcNow < deadline) await Task.Delay(50, TestContext.Current.CancellationToken);
        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Have, "a finished file is reached without another change event");
        quick.Dispose();
    }

    [Fact]
    public async Task BrowserPartialFilesAreIgnoredUntilTheFinalNameAppears()
    {
        using var page = Acquirer.Attach();
        PickupIs(true);
        await RunWithManualRows(("wanted.txt", Wanted));
        await Drop(DownloadsFolder, "wanted.txt.part", Wanted);
        await Drop(DownloadsFolder, "wanted.txt.crdownload", Wanted);
        await Drop(DownloadsFolder, "wanted.txt", []);
        var (pickup, log) = NewPickup();

        await pickup.Scan(CancellationToken.None);

        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Manual);
        TouchedOutside(log, DownloadsFolder).Should().OnlyContain(p => p.EndsWith("/wanted.txt"), "the placeholder is only measured, never read");
        log.Calls.Where(c => c.Method is "ReadFile" or "OpenFile" or "Open").Should().BeEmpty();
    }

    [Fact]
    public async Task ASymlinkWithTheExpectedName_IsNotFollowed()
    {
        using var page = Acquirer.Attach();
        PickupIs(true);
        await RunWithManualRows(("wanted.txt", Wanted));
        var elsewhere = TemporaryFileManager.CreateFolder().Path;
        await Drop(elsewhere, "real.bin", Wanted);
        File.CreateSymbolicLink(DownloadsFolder.Combine("wanted.txt").ToString(), elsewhere.Combine("real.bin").ToString());
        var (pickup, _) = NewPickup();

        await pickup.Scan(CancellationToken.None);

        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Manual);
    }

    [Fact]
    public async Task TwoArchivesSharingAFileName_CreditTheOneWhoseHashMatches()
    {
        using var page = Acquirer.Attach();
        PickupIs(true);
        await RunWithManualRows(("wanted.txt", Wanted), ("wanted.txt", SameLength));
        await Drop(DownloadsFolder, "wanted.txt", SameLength);
        var (pickup, _) = NewPickup();

        await pickup.Scan(CancellationToken.None);

        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Manual);
        Acquirer.Plan.Rows[1].State.Should().Be(ArchiveState.Have);
    }

    [Fact]
    public async Task WhenTheLinkFails_TheFileIsCopiedAndTheRowIsHeld()
    {
        using var page = Acquirer.Attach();
        PickupIs(true);
        await RunWithManualRows(("wanted.txt", Wanted));
        await Drop(DownloadsFolder, "wanted.txt", Wanted);
        var (pickup, _) = NewPickup();
        var attempts = 0;
        pickup.Link = (_, _) => { attempts++; return LibcLinks.EXDEV; };

        await pickup.Scan(CancellationToken.None);

        attempts.Should().Be(1);
        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Have);
        DownloadsFolder.Combine("wanted.txt").FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task AFolderThatDoesNotExist_IsQuietlySkipped()
    {
        using var page = Acquirer.Attach();
        PickupIs(true);
        await RunWithManualRows(("wanted.txt", Wanted));
        var (pickup, _) = NewPickup();

        await pickup.Scan(CancellationToken.None);

        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Manual);
    }

    [Fact]
    public async Task OnlyBrowserAndManualRowsAreExpected()
    {
        using var page = Acquirer.Attach();
        PickupIs(true);
        var row = new ArchivePlanRow(
            Entry(new ManualState(new Uri("https://example.test/m"), "get it"), Hash64(Wanted), Wanted.Length, "wanted.txt"),
            ArchiveState.Have, null, null, null);
        await Acquirer.Run(new ArchivePlan([row]), null, CancellationToken.None);
        await Drop(DownloadsFolder, "wanted.txt", Wanted);
        var (pickup, log) = NewPickup();

        await pickup.Scan(CancellationToken.None);

        TouchedOutside(log, DownloadsFolder).Should().BeEmpty("a row that is already held expects nothing");
    }

    [Fact]
    public async Task AFolderOfUnrelatedFiles_NeverGetsALinkCheck()
    {
        using var page = Acquirer.Attach();
        PickupIs(true);
        await RunWithManualRows(("wanted.txt", Wanted));
        for (var i = 0; i < 50; i++) await Drop(DownloadsFolder, $"other-{i}.bin", Wanted);
        var (pickup, log) = NewPickup();

        await pickup.Scan(CancellationToken.None);

        log.Calls.Where(c => c.Method == "IsPlainFile").Select(c => c.Path).Should().BeEmpty("no file in the folder has the expected name");
    }

    [Fact]
    public async Task AStagingFailure_IsReportedOnceAndTheSameBytesAreNotHashedAgain()
    {
        using var page = Acquirer.Attach();
        PickupIs(true);
        await RunWithManualRows(("wanted.txt", Wanted));
        await Drop(DownloadsFolder, "wanted.txt", Wanted);
        var (pickup, log) = NewPickup();
        var attempts = 0;
        pickup.Link = (_, _) => LibcLinks.EXDEV;
        pickup.Stage = (_, _, _, _) =>
        {
            attempts++;
            throw new IOException("No space left on device");
        };

        await pickup.Scan(CancellationToken.None);
        await pickup.Scan(CancellationToken.None);

        attempts.Should().Be(1, "a copy that failed is not retried until the file changes");
        log.Calls.Count(c => c.Method is "ReadFile" or "OpenFile" or "Open" && c.Path.EndsWith("/wanted.txt")).Should().Be(1);
        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Manual);
    }

    [Fact]
    public async Task AStagingFailure_DoesNotScheduleARescan()
    {
        using var page = Acquirer.Attach();
        PickupIs(true);
        await RunWithManualRows(("wanted.txt", Wanted));
        await Drop(DownloadsFolder, "wanted.txt", Wanted);
        var (pickup, log) = NewPickup(TimeSpan.FromMilliseconds(100));
        var attempts = 0;
        pickup.Stage = (_, _, _, _) =>
        {
            attempts++;
            throw new IOException("No space left on device");
        };

        await pickup.Scan(CancellationToken.None);
        await Task.Delay(1000, TestContext.Current.CancellationToken);

        attempts.Should().Be(1);
        pickup.Dispose();
    }

    [Fact]
    public async Task AStagingFailure_IsRetriedOnceTheMismatchMemoryIsForgotten()
    {
        using var page = Acquirer.Attach();
        PickupIs(true);
        await RunWithManualRows(("wanted.txt", Wanted));
        await Drop(DownloadsFolder, "wanted.txt", Wanted);
        var (pickup, _) = NewPickup();
        var fail = true;
        pickup.Stage = (source, inbox, link, token) => fail ? throw new IOException("No space left on device") : ArchiveInbox.Stage(source, inbox, link, token);
        await pickup.Scan(CancellationToken.None);

        fail = false;
        pickup.ForgetMismatches();
        await pickup.Scan(CancellationToken.None);

        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Have);
    }

    [Fact]
    public async Task AnUnreadableExpectedFile_DoesNotStopTheOthersFromBeingCredited()
    {
        using var page = Acquirer.Attach();
        PickupIs(true);
        await RunWithManualRows(("locked.txt", Wanted), ("fine.txt", SameLength));
        await Drop(DownloadsFolder, "locked.txt", Wanted);
        await Drop(DownloadsFolder, "fine.txt", SameLength);
        var locked = DownloadsFolder.Combine("locked.txt").ToString();
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var (pickup, _) = NewPickup();

            await pickup.Scan(CancellationToken.None);

            Acquirer.Plan.Rows.Single(r => r.Archive.Name == "fine.txt").State.Should().Be(ArchiveState.Have);
            Acquirer.Plan.Rows.Single(r => r.Archive.Name == "locked.txt").State.Should().Be(ArchiveState.Manual);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task AnUnreadableExpectedFile_IsNotOpenedAgainUntilItChanges()
    {
        using var page = Acquirer.Attach();
        PickupIs(true);
        await RunWithManualRows(("locked.txt", Wanted));
        await Drop(DownloadsFolder, "locked.txt", Wanted);
        var locked = DownloadsFolder.Combine("locked.txt").ToString();
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var (pickup, log) = NewPickup();

            await pickup.Scan(CancellationToken.None);
            await pickup.Scan(CancellationToken.None);

            log.Calls.Count(c => c.Method is "ReadFile" or "OpenFile" or "Open" && c.Path.EndsWith("/locked.txt")).Should().Be(1);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(50, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ThePickupWatchesOnlyOncePickingUpIsOnAndAnArchiveIsWaiting()
    {
        using var page = Acquirer.Attach();
        PickupIs(true);
        _ = DownloadsFolder;
        var (pickup, _) = NewPickup();
        pickup.IsWatching.Should().BeFalse("nothing follows the plan before Follow");

        pickup.Follow();
        pickup.IsWatching.Should().BeFalse("no row is waiting yet");
        await RunWithManualRows(("wanted.txt", Wanted));

        pickup.IsWatching.Should().BeTrue();
        pickup.Dispose();
    }

    [Fact]
    public async Task FollowingTwice_StillWatchesOnce()
    {
        using var page = Acquirer.Attach();
        PickupIs(true);
        _ = DownloadsFolder;
        await RunWithManualRows(("wanted.txt", Wanted));
        var (pickup, _) = NewPickup();

        pickup.Follow();
        pickup.Follow();

        pickup.IsWatching.Should().BeTrue();
        pickup.Dispose();
    }

    [Fact]
    public async Task TheWatchEnds_WhenTheLastWaitingRowIsHeld()
    {
        using var page = Acquirer.Attach();
        PickupIs(true);
        _ = DownloadsFolder;
        await RunWithManualRows(("wanted.txt", Wanted));
        var (pickup, _) = NewPickup();
        pickup.Follow();
        pickup.IsWatching.Should().BeTrue();

        await InboxFolder.Combine("wanted.txt").WriteAllBytesAsync(Wanted);
        await ServiceProvider.GetRequiredService<ArchiveInbox>().Scan(CancellationToken.None);
        Acquirer.Recheck();

        await Until(() => !pickup.IsWatching);
        pickup.IsWatching.Should().BeFalse();
        pickup.Dispose();
    }

    [Fact]
    public async Task SwitchingThePickupOff_EndsTheWatchWhileRowsAreStillWaiting()
    {
        using var page = Acquirer.Attach();
        PickupIs(true);
        _ = DownloadsFolder;
        await RunWithManualRows(("wanted.txt", Wanted));
        var (pickup, _) = NewPickup();
        pickup.Follow();
        pickup.IsWatching.Should().BeTrue();

        PickupIs(false);

        await Until(() => !pickup.IsWatching);
        pickup.IsWatching.Should().BeFalse();
        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Manual);

        PickupIs(true);
        await Until(() => pickup.IsWatching);
        pickup.IsWatching.Should().BeTrue("switching it back on starts the watch again");
        pickup.Dispose();
    }

    [Fact]
    public async Task ClosingThePage_EndsTheWatch()
    {
        var page = Acquirer.Attach();
        PickupIs(true);
        _ = DownloadsFolder;
        await RunWithManualRows(("wanted.txt", Wanted));
        var (pickup, _) = NewPickup();
        pickup.Follow();
        pickup.IsWatching.Should().BeTrue();

        page.Dispose();

        await Until(() => !pickup.IsWatching);
        pickup.IsWatching.Should().BeFalse("the acquirer forgets a plan nobody shows");
        pickup.Dispose();
    }

    [Fact]
    public async Task AMissingFolder_IsNotWatchedAndIsSaidOnce()
    {
        using var page = Acquirer.Attach();
        PickupIs(true);
        Settings.Update<WabbajackSettings>(s => s with { DownloadsFolder = DownloadsFolder.Combine("not-there").ToString() });
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var (pickup, _) = NewPickup(logger: factory.CreateLogger<DownloadsPickup>());

        pickup.Follow();
        await RunWithManualRows(("wanted.txt", Wanted));
        await pickup.Scan(CancellationToken.None);

        pickup.IsWatching.Should().BeFalse();
        logs.Messages.Where(m => m.Contains("does not exist")).Should().ContainSingle();
        pickup.Dispose();
    }

    [Fact]
    public async Task ChoosingAnotherFolderWhileWatching_MovesTheWatchToIt()
    {
        using var page = Acquirer.Attach();
        PickupIs(true);
        var folderA = DownloadsFolder;
        var folderB = folderA.Parent.Combine("browser-downloads");
        folderB.CreateDirectory();
        await RunWithManualRows(("wanted.txt", Wanted));
        var (pickup, _) = NewPickup(TimeSpan.FromMilliseconds(100));
        pickup.Follow();
        pickup.IsWatching.Should().BeTrue();
        pickup.WatchedFolder.Should().Be(folderA);

        Settings.Update<WabbajackSettings>(s => s with { DownloadsFolder = folderB.ToString() });
        await Drop(folderB, "wanted.txt", Wanted);

        await Until(() => Acquirer.Plan.Rows[0].State == ArchiveState.Have);
        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Have, "the watch follows the folder the user chose");
        pickup.IsWatching.Should().BeFalse("the row is held, so the watch ended");
        pickup.Dispose();
    }

    [Fact]
    public async Task ChoosingAnotherFolderWhileWatching_ReportsTheNewFolderAsWatched()
    {
        using var page = Acquirer.Attach();
        PickupIs(true);
        var folderB = DownloadsFolder.Parent.Combine("browser-downloads");
        folderB.CreateDirectory();
        await RunWithManualRows(("wanted.txt", Wanted));
        var (pickup, _) = NewPickup();
        pickup.Follow();

        Settings.Update<WabbajackSettings>(s => s with { DownloadsFolder = folderB.ToString() });

        pickup.IsWatching.Should().BeTrue();
        pickup.WatchedFolder.Should().Be(pickup.Folder);
        pickup.WatchedFolder.Should().Be(folderB);
        pickup.Dispose();
    }

    [Fact]
    public async Task ChoosingAMissingFolderWhileWatching_EndsTheWatch()
    {
        using var page = Acquirer.Attach();
        PickupIs(true);
        var folderA = DownloadsFolder;
        await RunWithManualRows(("wanted.txt", Wanted));
        var (pickup, _) = NewPickup();
        pickup.Follow();
        pickup.IsWatching.Should().BeTrue();

        Settings.Update<WabbajackSettings>(s => s with { DownloadsFolder = folderA.Parent.Combine("gone").ToString() });

        pickup.IsWatching.Should().BeFalse("a folder that does not exist cannot be watched");
        pickup.WatchedFolder.Should().BeNull();
        pickup.Dispose();
    }

    [Fact]
    public async Task AFileThatWasCompleteBeforeTheWatchStarted_IsPickedUpWithoutAnEvent()
    {
        using var page = Acquirer.Attach();
        PickupIs(true);
        _ = DownloadsFolder;
        await RunWithManualRows(("wanted.txt", Wanted));
        await Drop(DownloadsFolder, "wanted.txt", Wanted);
        var (pickup, _) = NewPickup();

        pickup.Follow();

        await Until(() => Acquirer.Plan.Rows[0].State == ArchiveState.Have);
        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Have);
        pickup.Dispose();
    }

    [Fact]
    public async Task AFileStillGrowingWhenSeen_IsPickedUpOnceItHasSettled()
    {
        using var page = Acquirer.Attach();
        PickupIs(true);
        _ = DownloadsFolder;
        await RunWithManualRows(("wanted.txt", Wanted));
        var (pickup, _) = NewPickup(TimeSpan.FromMilliseconds(300));
        pickup.Follow();

        await Drop(DownloadsFolder, "wanted.txt", Wanted, settled: false);

        await Until(() => Acquirer.Plan.Rows[0].State == ArchiveState.Have);
        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Have, "the last write raises no further event, so the scheduled look finds it");
        pickup.Dispose();
    }

    [Fact]
    public async Task APartFileIsIgnored_ThenPickedUpWhenRenamedToItsFinalName()
    {
        using var page = Acquirer.Attach();
        PickupIs(true);
        _ = DownloadsFolder;
        await RunWithManualRows(("wanted.txt", Wanted));
        var (pickup, _) = NewPickup(TimeSpan.FromMilliseconds(100));
        pickup.Follow();

        await Drop(DownloadsFolder, "wanted.txt.part", Wanted);
        await Task.Delay(600, TestContext.Current.CancellationToken);
        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Manual);

        File.Move(DownloadsFolder.Combine("wanted.txt.part").ToString(), DownloadsFolder.Combine("wanted.txt").ToString());
        File.SetLastWriteTimeUtc(DownloadsFolder.Combine("wanted.txt").ToString(), DateTime.UtcNow - TimeSpan.FromMinutes(5));

        await Until(() => Acquirer.Plan.Rows[0].State == ArchiveState.Have);
        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Have);
        pickup.Dispose();
    }

    [Fact]
    public async Task WhatWasSetAsideAsWrongBytes_IsReadAgainAfterTheWatchEndsAndStartsAgain()
    {
        using var page = Acquirer.Attach();
        PickupIs(true);
        _ = DownloadsFolder;
        await RunWithManualRows(("wanted.txt", Wanted));
        await Drop(DownloadsFolder, "wanted.txt", SameLength);
        var (pickup, log) = NewPickup();
        int Reads() => log.Calls.Count(c => c.Method is "ReadFile" or "OpenFile" or "Open" && c.Path.EndsWith("/wanted.txt"));
        pickup.Follow();
        await Until(() => Reads() == 1);

        PickupIs(false);
        await Until(() => !pickup.IsWatching);
        PickupIs(true);

        await Until(() => Reads() == 2);
        Reads().Should().Be(2);
        pickup.Dispose();
    }
}
