using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Library;
using NexusMods.Paths;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Native;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Library;

namespace SilverModManager.Wabbajack.Tests.Acquire;

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
        using var page = Acquirer.Attach();
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
        using var page = Acquirer.Attach();
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

        var held = Connection.Db.Datoms(LibraryFile.XxHash64, Hash64(Stray).XxHash64Value());
        held.Count.Should().Be(1);
    }

    [Fact]
    public async Task TheDownloadsFolderIsNeverLookedAt()
    {
        using var page = Acquirer.Attach();
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
        using var page = Acquirer.Attach();
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
        using var page = Acquirer.Attach();
        await RunWithManualRowFor(Wanted);
        Inbox.DebounceInterval = TimeSpan.FromMilliseconds(100);
        Inbox.StartWatching();

        await InboxFolder.Combine("dropped.txt").WriteAllBytesAsync(Wanted);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (Acquirer.Plan.Rows[0].State != ArchiveState.Have && DateTime.UtcNow < deadline) await Task.Delay(50);
        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Have);
    }

    [Fact]
    public async Task TheWatcherStartsWithTheFirstPlanThatHasWaitingRows_WhateverThePickupSetting()
    {
        using var page = Acquirer.Attach();
        Inbox.WatchOnceArchivesAreWaiting();
        Inbox.IsWatching.Should().BeFalse("nothing is waiting yet");

        await Acquirer.Run(new ArchivePlan([]), null, CancellationToken.None);
        Inbox.IsWatching.Should().BeFalse("an empty plan has nothing to wait for");

        await RunWithManualRowFor(Wanted);

        Inbox.IsWatching.Should().BeTrue();
        Inbox.WatchOnceArchivesAreWaiting();
    }

    private AbsolutePath NewFolder(string name)
    {
        var folder = TemporaryFileManager.CreateFolder().Path.Combine(name);
        folder.CreateDirectory();
        return folder;
    }

    private static (ulong Device, ulong Inode) Identity(AbsolutePath file)
    {
        LibcStat.TryStat(file.ToString(), out var stat).Should().Be(0);
        return (stat.Device, stat.Inode);
    }

    [Fact]
    public async Task StageWithARealLink_PutsTheSameFileInTheInboxAndLeavesTheSource()
    {
        var source = NewFolder("downloads").Combine("mod.7z");
        await source.WriteAllTextAsync("archive bytes");
        var inbox = NewFolder("inbox");

        await ArchiveInbox.Stage(source, inbox, LibcLinks.TryLink);

        source.FileExists.Should().BeTrue("the user's file stays where it was");
        Identity(inbox.Combine("mod.7z")).Should().Be(Identity(source));
        inbox.EnumerateFiles().Should().ContainSingle("no partial file is left behind");
    }

    [Fact]
    public async Task StageWhenTheLinkFails_CopiesUnderTheFinalNameAndLeavesTheSource()
    {
        var source = NewFolder("downloads").Combine("mod.7z");
        await source.WriteAllTextAsync("archive bytes");
        var inbox = NewFolder("inbox");
        var attempts = 0;

        await ArchiveInbox.Stage(source, inbox, (_, _) => { attempts++; return LibcLinks.EXDEV; });

        attempts.Should().Be(1);
        (await inbox.Combine("mod.7z").ReadAllTextAsync()).Should().Be("archive bytes");
        Identity(inbox.Combine("mod.7z")).Should().NotBe(Identity(source), "it is a copy, not a link");
        source.FileExists.Should().BeTrue();
        inbox.EnumerateFiles().Should().ContainSingle("no partial file is left behind");
    }

    [Fact]
    public async Task StageWithoutALinkFunction_Copies()
    {
        var source = NewFolder("downloads").Combine("mod.7z");
        await source.WriteAllTextAsync("archive bytes");
        var inbox = NewFolder("inbox");

        await ArchiveInbox.Stage(source, inbox);

        (await inbox.Combine("mod.7z").ReadAllTextAsync()).Should().Be("archive bytes");
        Identity(inbox.Combine("mod.7z")).Should().NotBe(Identity(source));
    }

    [Fact]
    public async Task StageRefusesASymlinkSource()
    {
        var real = NewFolder("downloads").Combine("real.7z");
        await real.WriteAllTextAsync("archive bytes");
        var link = real.Parent.Combine("mod.7z");
        File.CreateSymbolicLink(link.ToString(), real.ToString());
        var inbox = NewFolder("inbox");

        var act = () => ArchiveInbox.Stage(link, inbox, LibcLinks.TryLink);

        await act.Should().ThrowAsync<UserFacingException>();
        inbox.EnumerateFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task AFileAlreadyInTheInbox_KeepsItsBytes()
    {
        // Dropping a file from the opened inbox folder back onto the page names the same file as source and target.
        var inbox = NewFolder("inbox");
        var file = inbox.Combine("mod.7z");
        await file.WriteAllTextAsync("archive bytes");

        await ArchiveInbox.Stage(file, inbox);
        await ArchiveInbox.Stage(file, inbox, LibcLinks.TryLink);

        (await file.ReadAllTextAsync()).Should().Be("archive bytes");
        inbox.EnumerateFiles().Should().ContainSingle();
    }

    [Fact]
    public async Task ACopiedFile_LandsInTheInboxUnderItsOwnName()
    {
        var source = NewFolder("downloads").Combine("mod.7z");
        await source.WriteAllTextAsync("archive bytes");
        var inbox = NewFolder("inbox");

        await ArchiveInbox.Stage(source, inbox);

        (await inbox.Combine("mod.7z").ReadAllTextAsync()).Should().Be("archive bytes");
        source.FileExists.Should().BeTrue("the user's own copy stays where it was");
        inbox.EnumerateFiles().Should().ContainSingle("no partial file is left behind");
    }
}
