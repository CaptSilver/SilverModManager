using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Networking.Forums;
using SilverModManager.Networking.Forums.Auth;
using SilverModManager.Networking.Forums.Downloads;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Tests.Acquire.Hosts;

namespace SilverModManager.Wabbajack.Tests.Acquire;

/// <summary>
/// A list that gives a ModDB page through its Manual downloader: the run fetches it with the ModDB job, checks the
/// hash like any other download, and leaves the row to the user, with the list's prompt, when that does not work.
/// </summary>
public class ArchiveAcquirerManualHostTests(ITestOutputHelper helper) : AcquireTestBase<ArchiveAcquirerManualHostTests>(helper)
{
    private const string Prompt = "Click the big Download button";
    private static readonly Uri AddonPage = new("https://www.moddb.com/mods/cool-mod/addons/cool-patch");
    private static readonly byte[] Listed = "the file the list was made with"u8.ToArray();

    private byte[] _served = Listed;
    private bool _siteIsDown;

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        var client = new HttpClient(new FakeHost((request, _) =>
        {
            if (_siteIsDown) return FakeHost.Html("down", System.Net.HttpStatusCode.ServiceUnavailable);
            if (request.Uri == AddonPage) return FakeHost.Html("<a href=\"/downloads/start/1\">Download</a>");
            if (request.Uri.AbsolutePath == "/downloads/start/1") return FakeHost.Html("<a href=\"/downloads/mirror/1/2/abc/\">Mirror</a>");
            if (request.Uri.AbsolutePath.StartsWith("/downloads/mirror/", StringComparison.Ordinal)) return FakeHost.Bytes(_served);
            return FakeHost.Html("not found", System.Net.HttpStatusCode.NotFound);
        }));
        return base.AddServices(services)
            .AddSingleton(client)
            .AddSingleton(sp => new ForumFilesClient(client, sp.GetRequiredService<ForumOAuth>(), sp.GetRequiredService<IForumCredentials>(),
                TimeProvider.System, NullLogger<ForumFilesClient>.Instance));
    }

    private IArchiveAcquirer Acquirer => ServiceProvider.GetRequiredService<IArchiveAcquirer>();

    private static ArchivePlanRow ManualRow(Uri url)
        => new(Entry(new ManualState(url, Prompt), Hash64(Listed), Listed.Length, "Cool_Patch.7z"), ArchiveState.Automatic, null, url, null);

    [Fact]
    public async Task ThePageIsFetchedWithTheModDbJobAndTheRowIsHeld()
    {
        using var page = Acquirer.Attach();

        await Acquirer.Run(new ArchivePlan([ManualRow(AddonPage)]), null, CancellationToken.None);

        var row = Acquirer.Plan.Rows[0];
        row.State.Should().Be(ArchiveState.Have);
        row.Reason.Should().BeNull();
    }

    [Fact]
    public async Task AFileThatIsNotTheListedOneIsNotAcceptedAndTheRowKeepsTheListsPromptAndPage()
    {
        _served = "some other file"u8.ToArray();
        using var page = Acquirer.Attach();

        await Acquirer.Run(new ArchivePlan([ManualRow(AddonPage)]), null, CancellationToken.None);

        var row = Acquirer.Plan.Rows[0];
        row.State.Should().Be(ArchiveState.Manual);
        row.Library.Should().BeNull();
        row.Reason.Should().Contain("hash mismatch").And.Contain(Prompt);
        row.Page.Should().Be(AddonPage);
    }

    [Fact]
    public async Task AFailedJobLeavesTheRowWithTheJobsReasonTheListsPromptAndTheListsPage()
    {
        _siteIsDown = true;
        using var page = Acquirer.Attach();

        await Acquirer.Run(new ArchivePlan([ManualRow(AddonPage)]), null, CancellationToken.None);

        var row = Acquirer.Plan.Rows[0];
        row.State.Should().Be(ArchiveState.Manual);
        row.Reason.Should().Contain("503").And.Contain(Prompt);
        row.Page.Should().Be(AddonPage);
    }

    [Fact]
    public async Task AFailedRowWithoutAPageStillSendsTheUserToTheListsAddress()
    {
        _siteIsDown = true;
        using var page = Acquirer.Attach();
        var row = ManualRow(AddonPage) with { Page = null };

        await Acquirer.Run(new ArchivePlan([row]), null, CancellationToken.None);

        Acquirer.Plan.Rows[0].Page.Should().Be(AddonPage);
    }

    [Fact]
    public async Task AFailedRowThatNeverHadAPromptShowsOnlyTheJobsReason()
    {
        _siteIsDown = true;
        using var page = Acquirer.Attach();
        var entry = Entry(new ModDbState(AddonPage), Hash64(Listed), Listed.Length, "Cool_Patch.7z");

        await Acquirer.Run(new ArchivePlan([new ArchivePlanRow(entry, ArchiveState.Automatic, null, null, null)]), null, CancellationToken.None);

        Acquirer.Plan.Rows[0].Reason.Should().Be("ModDB answered 503; download it in your browser");
    }
}
