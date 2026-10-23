using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Library;
using NexusMods.Paths;
using NexusMods.Sdk.Library;
using NexusMods.Wabbajack.Acquire;
using NexusMods.Wabbajack.Format;
using NexusMods.Wabbajack.Library;

namespace NexusMods.Wabbajack.Tests.Acquire;

public class ArchiveInboxTests(ITestOutputHelper helper) : AcquireTestBase<ArchiveInboxTests>(helper)
{
    private IArchiveAcquirer Acquirer => ServiceProvider.GetRequiredService<IArchiveAcquirer>();
    private ArchiveInbox Inbox => ServiceProvider.GetRequiredService<ArchiveInbox>();
    private AbsolutePath InboxFolder => ServiceProvider.GetRequiredService<WabbajackPaths>().Inbox;

    private static readonly byte[] Wanted = "a file the list wants"u8.ToArray();
    private static readonly byte[] Stray = "a file nobody asked for"u8.ToArray();

    private async Task RunWithManualRowFor(byte[] content)
    {
        var row = new ArchivePlanRow(Entry(new ManualState(new Uri("https://example.test/m"), "get it"), Hash64(content), content.Length, "wanted.txt"),
            ArchiveState.Manual, "get it", new Uri("https://example.test/m"), null);
        await Acquirer.Run(new ArchivePlan([row]), null, CancellationToken.None);
    }

    [Fact]
    public async Task AFileDroppedInTheInboxIsMatchedByHashAndTheRowBecomesHeld()
    {
        await RunWithManualRowFor(Wanted);
        await InboxFolder.Combine("whatever-the-browser-called-it.txt").WriteAllBytesAsync(Wanted);

        var unmatched = await Inbox.Scan(CancellationToken.None);

        unmatched.Should().BeEmpty();
        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Have);
        Acquirer.Plan.Rows[0].Library.Should().NotBeNull();
        InboxFolder.Combine("whatever-the-browser-called-it.txt").FileExists.Should().BeFalse("a matched file now lives in the library");
    }

    [Fact]
    public async Task AFileThatMatchesNothingIsListedAndKept()
    {
        await RunWithManualRowFor(Wanted);
        await InboxFolder.Combine("stray.txt").WriteAllBytesAsync(Stray);

        var unmatched = await Inbox.Scan(CancellationToken.None);

        unmatched.Should().ContainSingle().Which.Should().Be(new UnmatchedInbox("stray.txt", Hash64(Stray)));
        InboxFolder.Combine("stray.txt").FileExists.Should().BeTrue();
        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Manual);
    }

    [Fact]
    public async Task AScanTwiceDoesNotAddTheSameFileToTheLibraryTwice()
    {
        await RunWithManualRowFor(Wanted);
        await InboxFolder.Combine("stray.txt").WriteAllBytesAsync(Stray);

        await Inbox.Scan(CancellationToken.None);
        await Inbox.Scan(CancellationToken.None);

        var held = Connection.Db.Datoms(LibraryFile.XxHash64, Hash64(Stray));
        held.Count.Should().Be(1);
    }

    [Fact]
    public async Task TheDownloadsFolderIsNeverLookedAt()
    {
        await RunWithManualRowFor(Wanted);
        var home = TemporaryFileManager.CreateFolder();
        var downloads = home.Path.Combine("Downloads");
        downloads.CreateDirectory();
        await downloads.Combine("wanted.txt").WriteAllBytesAsync(Wanted);

        var unmatched = await Inbox.Scan(CancellationToken.None);

        unmatched.Should().BeEmpty();
        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Manual);
        ServiceProvider.GetRequiredService<ILibraryArchiveLocator>().FindByXxHash64(Connection.Db, Hash64(Wanted)).Should().BeNull();
        InboxFolder.Parent.Should().NotBe(home.Path);
    }

    [Fact]
    public async Task AFileThatIsALinkOutOfTheInboxIsNotFollowed()
    {
        await RunWithManualRowFor(Wanted);
        var outside = TemporaryFileManager.CreateFolder();
        await outside.Path.Combine("wanted.txt").WriteAllBytesAsync(Wanted);
        File.CreateSymbolicLink(InboxFolder.Combine("link.txt").ToString(), outside.Path.Combine("wanted.txt").ToString());

        var unmatched = await Inbox.Scan(CancellationToken.None);

        unmatched.Should().BeEmpty();
        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Manual);
    }

    [Fact]
    public async Task TheWatcherScansAfterAFileIsDropped()
    {
        await RunWithManualRowFor(Wanted);
        Inbox.DebounceInterval = TimeSpan.FromMilliseconds(100);
        Inbox.StartWatching();

        await InboxFolder.Combine("dropped.txt").WriteAllBytesAsync(Wanted);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (Acquirer.Plan.Rows[0].State != ArchiveState.Have && DateTime.UtcNow < deadline) await Task.Delay(50);
        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Have);
    }
}
