using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Acquire.Hosts;

namespace SilverModManager.Wabbajack.Tests.Acquire.Hosts;

/// <summary>What a host download tells the log: each step at Trace, and one Warning naming the archive when it fails.</summary>
public class HostDownloadLoggingTests(IServiceProvider provider, TemporaryFileManager files) : HostedJobTestBase(provider, files)
{
    private const string ArchiveName = "Cool_Mod.7z";
    private static readonly Uri ModDbPage = new("https://www.moddb.com/mods/cool-mod/addons/cool-patch?token=secret");
    private static readonly Uri ModDbPageBare = new("https://www.moddb.com/mods/cool-mod/addons/cool-patch");
    private static readonly Uri Mirror = new("https://www.moddb.com/downloads/mirror/123456/118/0123abcd/?referer=x");
    private static readonly Uri MediaFirePage = new("https://www.mediafire.com/file/abc123/Cool_Mod.7z/file?key=secret");

    private static string Page(string body) => $"<html><body>{body}</body></html>";

    private ModDbDownloadJob ModDb(FakeHost host, AbsolutePath destination, ListLogger<ModDbDownloadJob> log) => new()
    {
        Client = new HttpClient(host),
        Logger = log,
        Throttle = new HostThrottle(),
        Url = ModDbPage,
        FileName = ArchiveName,
        Destination = destination,
    };

    private MediaFireDownloadJob MediaFire(FakeHost host, AbsolutePath destination, ListLogger<MediaFireDownloadJob> log) => new()
    {
        Client = new HttpClient(host),
        Logger = log,
        Throttle = new HostThrottle(),
        Url = MediaFirePage,
        FileName = ArchiveName,
        Destination = destination,
    };

    private static void WarnedOnceAbout(IEnumerable<string> warnings, string host, string page, string reason)
    {
        var warning = warnings.Should().ContainSingle().Subject;
        warning.Should().Contain(ArchiveName).And.Contain(host).And.Contain(page).And.Contain(reason);
        warning.Should().NotContain("secret").And.NotContain("?");
    }

    [Fact]
    public async Task ModDbSaysWhichMirrorItPickedAtTraceWithoutTheQuery()
    {
        var log = new ListLogger<ModDbDownloadJob>();
        var host = new FakeHost((request, _) => request.Uri.AbsolutePath.StartsWith("/downloads/mirror/", StringComparison.Ordinal)
            ? FakeHost.Bytes(Content(100))
            : FakeHost.Html(Page($"<a href=\"{Mirror}\">Download</a>")));
        await using var destination = Files.CreateFile();

        await Begin(ModDb(host, destination, log));

        var trace = string.Join('\n', log.Messages(LogLevel.Trace));
        trace.Should().Contain("https://www.moddb.com/downloads/mirror/123456/118/0123abcd/").And.Contain(ModDbPageBare.ToString());
        trace.Should().NotContain("secret").And.NotContain("referer=x");
        log.Messages(LogLevel.Warning).Should().BeEmpty();
    }

    [Fact]
    public async Task ModDbWithNoMirrorLinkWarnsOnceWithArchiveHostPageAndReason()
    {
        var log = new ListLogger<ModDbDownloadJob>();
        var host = new FakeHost((_, _) => FakeHost.Html(Page("<p>nothing</p>")));
        await using var destination = Files.CreateFile();

        var act = async () => await Begin(ModDb(host, destination, log));

        await act.Should().ThrowAsync<Exception>();
        WarnedOnceAbout(log.Messages(LogLevel.Warning), "ModDB", ModDbPageBare.ToString(), "mirror link");
    }

    [Fact]
    public async Task ModDbAnsweringWithAnErrorStatusWarnsOnceWithTheStatus()
    {
        var log = new ListLogger<ModDbDownloadJob>();
        var host = new FakeHost((_, _) => FakeHost.Html("gone", HttpStatusCode.NotFound));
        await using var destination = Files.CreateFile();

        var act = async () => await Begin(ModDb(host, destination, log));

        await act.Should().ThrowAsync<Exception>();
        WarnedOnceAbout(log.Messages(LogLevel.Warning), "ModDB", ModDbPageBare.ToString(), "404");
    }

    [Fact]
    public async Task MediaFireSaysWhichLinkItPickedAtTraceWithoutTheQuery()
    {
        var log = new ListLogger<MediaFireDownloadJob>();
        var host = new FakeHost((request, _) => request.Uri.Host == "www.mediafire.com"
            ? FakeHost.Html(Page("<a id=\"downloadButton\" href=\"https://download1.mediafire.com/xyz/Cool_Mod.7z?sig=secret\">Download</a>"))
            : FakeHost.Bytes(Content(100)));
        await using var destination = Files.CreateFile();

        await Begin(MediaFire(host, destination, log));

        var trace = string.Join('\n', log.Messages(LogLevel.Trace));
        trace.Should().Contain("https://download1.mediafire.com/xyz/Cool_Mod.7z");
        trace.Should().NotContain("secret");
    }

    [Fact]
    public async Task MediaFireRemovedFileWarnsOnceWithArchiveHostPageAndReason()
    {
        var log = new ListLogger<MediaFireDownloadJob>();
        var host = new FakeHost((_, _) => FakeHost.Html(Page("Invalid or Deleted File.")));
        await using var destination = Files.CreateFile();

        var act = async () => await Begin(MediaFire(host, destination, log));

        await act.Should().ThrowAsync<Exception>();
        WarnedOnceAbout(log.Messages(LogLevel.Warning), "MediaFire", "https://www.mediafire.com/file/abc123/Cool_Mod.7z/file", "removed");
    }

    [Fact]
    public async Task GoogleDriveWebPageWarnsOnce()
    {
        var log = new ListLogger<GoogleDriveDownloadJob>();
        var host = new FakeHost((_, _) => FakeHost.Html(Page("<p>hello</p>")));
        await using var destination = Files.CreateFile();
        var job = new GoogleDriveDownloadJob
        {
            Client = new HttpClient(host),
            Logger = log,
            Throttle = new HostThrottle(),
            Url = GoogleDriveDownloadJob.PageFor("file1"),
            FileId = "file1",
            FileName = ArchiveName,
            Destination = destination,
        };

        var act = async () => await Begin(job);

        await act.Should().ThrowAsync<Exception>();
        WarnedOnceAbout(log.Messages(LogLevel.Warning), "Google Drive", "https://drive.google.com/uc", "web page");
    }

    [Fact]
    public async Task HeaderedHttpStatusFailureWarnsOnce()
    {
        var log = new ListLogger<HeaderedHttpDownloadJob>();
        var host = new FakeHost((_, _) => FakeHost.Html("no", HttpStatusCode.Forbidden));
        await using var destination = Files.CreateFile();
        var job = new HeaderedHttpDownloadJob
        {
            Client = new HttpClient(host),
            Logger = log,
            Throttle = new HostThrottle(),
            Url = new Uri("https://files.test/mods/a.7z?sig=secret"),
            Headers = ["Accept: */*"],
            FileName = ArchiveName,
            Destination = destination,
        };

        var act = async () => await Begin(job);

        await act.Should().ThrowAsync<Exception>();
        WarnedOnceAbout(log.Messages(LogLevel.Warning), "files.test", "https://files.test/mods/a.7z", "403");
    }

    [Fact]
    public async Task MegaRefusalWarnsOnce()
    {
        var log = new ListLogger<MegaDownloadJob>();
        var host = new FakeHost((_, _) => FakeHost.Html("x"));
        await using var destination = Files.CreateFile();
        var job = new MegaDownloadJob
        {
            Client = new HttpClient(host),
            Logger = log,
            Throttle = new HostThrottle(),
            Url = new Uri("https://mega.nz/folder/abc?x=secret"),
            FileName = ArchiveName,
            Destination = destination,
        };

        var act = async () => await Begin(job);

        await act.Should().ThrowAsync<Exception>();
        WarnedOnceAbout(log.Messages(LogLevel.Warning), "MEGA", "https://mega.nz/folder/abc", "not a MEGA file link");
    }

    [Fact]
    public async Task AFlakyMegaAnswerIsWarnedAboutOnceNotOncePerAttempt()
    {
        var log = new ListLogger<MegaDownloadJob>();
        var host = new FakeHost((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await using var destination = Files.CreateFile();
        var job = new MegaDownloadJob
        {
            Client = new HttpClient(host),
            Logger = log,
            Throttle = new HostThrottle(),
            Url = new Uri("https://mega.nz/file/AbCdEf12#" + MegaLinkTests.Base64Url(MegaLinkTests.FileKey)),
            FileName = ArchiveName,
            Destination = destination,
            Attempts = 4,
            RetryDelay = TimeSpan.FromMilliseconds(1),
        };

        var act = async () => await Begin(job);

        await act.Should().ThrowAsync<Exception>();
        host.Seen.Count.Should().Be(4);
        WarnedOnceAbout(log.Messages(LogLevel.Warning), "MEGA", "https://mega.nz/file/AbCdEf12", "500");
        log.Messages(LogLevel.Trace).Should().Contain(m => m.Contains("attempt 1 of 4"));
    }

    [Fact(Timeout = 60000)]
    public async Task ADownloadCancelledWhileWaitingForASlotIsNotAWarning()
    {
        var log = new ListLogger<ModDbDownloadJob>();
        var throttle = new HostThrottle();
        using var first = await throttle.Enter("ModDB", CancellationToken.None);
        using var second = await throttle.Enter("ModDB", CancellationToken.None);
        var host = new FakeHost((_, _) => FakeHost.Html(""));
        await using var destination = Files.CreateFile();
        var task = Begin(ModDb(host, destination, log) with { Throttle = throttle });

        await Task.Delay(200);
        task.Job.AsContext().Cancel();
        var act = async () => await task;

        await act.Should().ThrowAsync<OperationCanceledException>();
        log.Messages(LogLevel.Warning).Should().BeEmpty();
    }
}
