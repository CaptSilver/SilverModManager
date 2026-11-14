using R3;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using SilverModManager.Library.Pickup;
using SilverModManager.Sdk.Settings;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;

namespace SilverModManager.Wabbajack.Tests.Acquire;

public class WabbajackExpectedDownloadsTests(ITestOutputHelper helper) : AcquireTestBase<WabbajackExpectedDownloadsTests>(helper)
{
    private static readonly byte[] Wanted = "the bytes the list expects"u8.ToArray();

    private IArchiveAcquirer Acquirer => ServiceProvider.GetRequiredService<IArchiveAcquirer>();
    private ISettingsManager Settings => ServiceProvider.GetRequiredService<ISettingsManager>();

    private AbsolutePath DownloadsFolder
    {
        get
        {
            var folder = NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath(Settings.Get<WabbajackSettings>().DownloadsFolder);
            folder.CreateDirectory();
            return folder;
        }
    }

    private async Task RunWith(ArchiveState state, string name, byte[] content, DownloadState? downloadState = null)
    {
        var row = new ArchivePlanRow(
            Entry(downloadState ?? new ManualState(new Uri("https://example.test/m"), "get it"), Hash64(content), content.Length, name),
            state, "get it", new Uri("https://example.test/m"), null);
        await Acquirer.Run(new ArchivePlan([row]), null, CancellationToken.None);
    }

    private (WabbajackExpectedDownloads Mirror, ExpectedDownloads Expected) NewMirror()
    {
        var expected = new ExpectedDownloads();
        var mirror = new WabbajackExpectedDownloads(Acquirer, expected, ServiceProvider.GetRequiredService<ArchiveInbox>());
        return (mirror, expected);
    }

    [Theory]
    [InlineData(ArchiveState.Manual)]
    [InlineData(ArchiveState.Browser)]
    [InlineData(ArchiveState.SignIn)]
    public async Task WaitingRowsAreExpected(ArchiveState state)
    {
        using var page = Acquirer.Attach();
        await RunWith(state, "wanted.txt", Wanted);
        var (mirror, expected) = NewMirror();
        using var _ = mirror;

        mirror.Follow();

        var download = expected.Current.Should().ContainSingle().Subject;
        download.FileName.Should().Be("wanted.txt");
        download.Size.Should().Be(Wanted.Length);
        download.XxHash64.Should().Be(Hash64(Wanted));
        download.Sink.Should().BeSameAs(mirror);
    }

    [Fact]
    public async Task ASettledRowIsNotExpected()
    {
        using var page = Acquirer.Attach();
        await RunWith(ArchiveState.Have, "wanted.txt", Wanted);
        var (mirror, expected) = NewMirror();
        using var _ = mirror;

        mirror.Follow();

        expected.Current.Should().BeEmpty();
    }

    [Fact]
    public async Task ARowThatIsHeldStopsBeingExpected()
    {
        using var page = Acquirer.Attach();
        await RunWith(ArchiveState.Manual, "wanted.txt", Wanted);
        var (mirror, expected) = NewMirror();
        using var _ = mirror;
        mirror.Follow();
        expected.Current.Should().HaveCount(1);

        await RunWith(ArchiveState.Have, "wanted.txt", Wanted);

        expected.Current.Should().BeEmpty();
    }

    [Fact]
    public async Task ClosingThePageEndsTheExpectation()
    {
        var page = Acquirer.Attach();
        await RunWith(ArchiveState.Manual, "wanted.txt", Wanted);
        var (mirror, expected) = NewMirror();
        using var _ = mirror;
        mirror.Follow();
        expected.Current.Should().HaveCount(1);

        page.Dispose();

        expected.Current.Should().BeEmpty("the acquirer forgets a plan nobody shows");
    }

    [Fact]
    public async Task AReplannedRowThatStillWaitsIsNotEndedAndExpectedAgain()
    {
        using var page = Acquirer.Attach();
        await RunWith(ArchiveState.Manual, "wanted.txt", Wanted);
        var (mirror, expected) = NewMirror();
        using var _ = mirror;
        mirror.Follow();
        var changes = 0;
        using var subscription = expected.Changed.Subscribe(_ => changes++);

        await RunWith(ArchiveState.Manual, "wanted.txt", Wanted);

        changes.Should().Be(0, "ending and re-adding the same row would stop the folder watch for a moment");
        expected.Current.Should().HaveCount(1);
    }

    [Fact]
    public async Task AFileThatTurnsUpInDownloadsCreditsTheRowAndStaysThere()
    {
        using var page = Acquirer.Attach();
        Settings.Update<WabbajackSettings>(s => s with { PickUpFromDownloads = true });
        await RunWith(ArchiveState.Manual, "wanted.txt", Wanted);
        var file = DownloadsFolder.Combine("wanted.txt");
        await file.WriteAllBytesAsync(Wanted);
        File.SetLastWriteTimeUtc(file.ToString(), DateTime.UtcNow - TimeSpan.FromMinutes(5));
        var (mirror, expected) = NewMirror();
        using var _ = mirror;
        mirror.Follow();
        using var pickup = new DownloadsPickup(new WabbajackPickupSettings(Settings), expected, NexusMods.Paths.FileSystem.Shared, NullLogger<DownloadsPickup>.Instance)
        {
            DebounceInterval = TimeSpan.FromMinutes(1),
        };

        await pickup.Scan(CancellationToken.None);

        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Have);
        file.FileExists.Should().BeTrue("nothing in Downloads is deleted or moved");
        ServiceProvider.GetRequiredService<WabbajackPaths>().Inbox.EnumerateFiles().Should().BeEmpty("the inbox scan removes the copy the app staged once it is in the library");
    }

    [Fact]
    public void ThePickupIsOffAndFollowsTheXdgFolderByDefault()
    {
        var settings = WabbajackSettings.CreateDefault(ServiceProvider);

        settings.PickUpFromDownloads.Should().BeFalse();
        settings.DownloadsFolder.Should().BeEmpty();
    }

    [Fact]
    public void TheAdapterReadsAndAnnouncesTheWabbajackSetting()
    {
        var adapter = new WabbajackPickupSettings(Settings);
        var announced = 0;
        using var subscription = adapter.Changes.Subscribe(_ => announced++);

        Settings.Update<WabbajackSettings>(s => s with { PickUpFromDownloads = true, DownloadsFolder = "/somewhere" });

        adapter.PickUpFromDownloads.Should().BeTrue();
        adapter.DownloadsFolder.Should().Be("/somewhere");
        announced.Should().BeGreaterThan(0);
        Settings.Update<WabbajackSettings>(s => s with { PickUpFromDownloads = false, DownloadsFolder = "" });
    }
}
