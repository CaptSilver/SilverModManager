using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using SilverModManager.Abstractions.Library;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Acquire.Hosts;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Wabbajack.Tests.Acquire.Hosts;

public class MegaDownloadJobTests(IServiceProvider provider, TemporaryFileManager files) : HostedJobTestBase(provider, files)
{
    private static readonly byte[] Key = MegaLinkTests.FileKey;
    private static readonly Uri PageUrl = new($"https://mega.nz/file/AbCdEf12#{MegaLinkTests.Base64Url(Key)}");
    private static readonly Uri DownloadUrl = new("https://gfs270n123.userstorage.mega.co.nz/dl/token");
    private static readonly MegaLink Link = MegaLink.TryParse(PageUrl, out var parsed) ? parsed! : throw new InvalidOperationException();

    private static string Attributes(string name)
    {
        var text = Encoding.UTF8.GetBytes("MEGA" + $"{{\"n\":\"{name}\"}}");
        var padded = new byte[(text.Length + 15) / 16 * 16];
        text.CopyTo(padded, 0);
        using var aes = Aes.Create();
        aes.Key = Link.AesKey;
        return MegaLinkTests.Base64Url(aes.EncryptCbc(padded, new byte[16], PaddingMode.None));
    }

    private static string ApiAnswer(long size, string name = "Cool Mod.7z") => $"[{{\"s\":{size},\"at\":\"{Attributes(name)}\",\"g\":\"{DownloadUrl}\"}}]";

    private static byte[] Encrypt(byte[] plain) => Aes128CtrStreamTests.Reference(Link.AesKey, Link.Nonce, plain);

    private MegaDownloadJob JobFor(FakeHost host, AbsolutePath destination, Uri? url = null, HostThrottle? throttle = null) => new()
    {
        Client = new HttpClient(host),
        Logger = NullLogger<MegaDownloadJob>.Instance,
        Throttle = throttle ?? new HostThrottle(),
        Url = url ?? PageUrl,
        Destination = destination,
        RetryDelay = TimeSpan.FromMilliseconds(1),
    };

    private static FakeHost ServingFile(byte[] plain, Func<SeenRequest, int, HttpResponseMessage?>? override_ = null) => new((request, earlier) =>
    {
        if (override_?.Invoke(request, earlier) is { } custom) return custom;
        return request.Uri.Host == "g.api.mega.co.nz" ? FakeHost.Json(ApiAnswer(plain.Length)) : FakeHost.Bytes(Encrypt(plain));
    });

    [Fact]
    public async Task TheDecryptedFileIsWrittenAndAddedToTheLibraryUnderItsNameAndPage()
    {
        var plain = Content(200_001);
        var host = ServingFile(plain);
        await using var destination = Files.CreateFile();

        var file = await Provider.GetRequiredService<ILibraryService>().AddDownload(Begin(JobFor(host, destination)), UnassignedAcquisition.Instance);

        (await destination.Path.ReadAllBytesAsync()).Should().Equal(plain);
        file.XxHash64Identity().Should().Be(Hash(plain));
        file.FileName.Should().Be(RelativePath.FromUnsanitizedInput("Cool Mod.7z"));
        file.AsLibraryItem().TryGetAsDownloadedFile(out var downloaded).Should().BeTrue();
        downloaded.DownloadPageUri.Should().Be(PageUrl);
    }

    [Fact]
    public async Task TheApiIsAskedForTheFileIdWithTheDownloadFlag()
    {
        var plain = Content(100);
        var host = ServingFile(plain);
        await using var destination = Files.CreateFile();

        await Begin(JobFor(host, destination));

        var api = host.Seen.Single(r => r.Uri.Host == "g.api.mega.co.nz");
        api.Method.Should().Be(HttpMethod.Post);
        api.Body.Should().Be("[{\"a\":\"g\",\"g\":1,\"p\":\"AbCdEf12\"}]");
        host.Seen.Single(r => r.Uri == DownloadUrl).Method.Should().Be(HttpMethod.Get);
    }

    [Fact]
    public async Task AFileTheApiDoesNotKnowFailsNamingTheLinkAndIsNotRetried()
    {
        var host = new FakeHost((_, _) => FakeHost.Json("[-9]"));
        await using var destination = Files.CreateFile();

        var act = async () => await Begin(JobFor(host, destination));

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("no file").And.Contain("browser");
        host.Seen.Should().HaveCount(1);
        destination.Path.FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task ABareNumberIsReadAsAnErrorToo()
    {
        var host = new FakeHost((_, _) => FakeHost.Json("-9"));
        await using var destination = Files.CreateFile();

        var act = async () => await Begin(JobFor(host, destination));

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("no file");
    }

    [Fact]
    public async Task TemporarilyUnavailableIsRetriedUntilTheApiAnswers()
    {
        var plain = Content(1000);
        var host = ServingFile(plain, (request, earlier) => request.Uri.Host == "g.api.mega.co.nz" && earlier < 2 ? FakeHost.Json("[-18]") : null);
        await using var destination = Files.CreateFile();

        await Begin(JobFor(host, destination));

        host.Count(r => r.Uri.Host == "g.api.mega.co.nz").Should().Be(3);
        (await destination.Path.ReadAllBytesAsync()).Should().Equal(plain);
    }

    [Fact]
    public async Task AnApiThatStaysUnavailableFailsAfterTheAttemptsSayingSo()
    {
        var host = new FakeHost((_, _) => FakeHost.Json("[-18]"));
        await using var destination = Files.CreateFile();
        var job = JobFor(host, destination) with { Attempts = 3 };

        var act = async () => await Begin(job);

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("temporarily unavailable");
        host.Seen.Should().HaveCount(3);
    }

    [Fact]
    public async Task AnExhaustedBandwidthQuotaFromTheApiSendsTheUserToTheirBrowser()
    {
        var host = new FakeHost((_, _) => FakeHost.Json("[-17]"));
        await using var destination = Files.CreateFile();

        var act = async () => await Begin(JobFor(host, destination));

        (await act.Should().ThrowAsync<Exception>()).Which.Message
            .Should().Be("MEGA's download quota is used up; try later or download in your browser");
        host.Seen.Should().HaveCount(1);
    }

    [Fact]
    public async Task AnApiThatKeepsAnsweringAnHttpErrorGivesAPlainReason()
    {
        var host = new FakeHost((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await using var destination = Files.CreateFile();
        var job = JobFor(host, destination) with { Attempts = 3 };

        var act = async () => await Begin(job);

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Be("MEGA answered 500; download it in your browser");
        host.Seen.Should().HaveCount(3);
    }

    [Fact]
    public async Task AnOverQuotaHttpAnswerFromTheApiGetsTheQuotaMessage()
    {
        var host = new FakeHost((_, _) => new HttpResponseMessage((HttpStatusCode)509));
        await using var destination = Files.CreateFile();
        var job = JobFor(host, destination) with { Attempts = 2 };

        var act = async () => await Begin(job);

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Be(MegaDownloadJob.QuotaMessage);
    }

    [Fact]
    public async Task AnOverQuotaAnswerFromTheDownloadServerSendsTheUserToTheirBrowser()
    {
        var host = ServingFile([], (request, _) => request.Uri == DownloadUrl ? new HttpResponseMessage((HttpStatusCode)509) : null);
        await using var destination = Files.CreateFile();

        var act = async () => await Begin(JobFor(host, destination));

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("download quota is used up");
        destination.Path.FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task AFileShorterThanTheApiSaidFailsAndLeavesNothingBehind()
    {
        var plain = Content(5000);
        var host = new FakeHost((request, _) => request.Uri.Host == "g.api.mega.co.nz"
            ? FakeHost.Json(ApiAnswer(plain.Length + 10))
            : FakeHost.Bytes(Encrypt(plain)));
        await using var destination = Files.CreateFile();

        var act = async () => await Begin(JobFor(host, destination));

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("5000");
        destination.Path.FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task ALinkThatIsNotAFileLinkIsRefusedBeforeAnyRequest()
    {
        var host = new FakeHost((_, _) => FakeHost.Json("[]"));
        await using var destination = Files.CreateFile();

        var act = async () => await Begin(JobFor(host, destination, new Uri("https://mega.nz/folder/abc#def")));

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("MEGA file link");
        host.Seen.Should().BeEmpty();
    }

    [Fact]
    public async Task ProgressReachesTheFullSize()
    {
        var plain = Content(3_000_000);
        var host = ServingFile(plain);
        await using var destination = Files.CreateFile();
        var task = Begin(JobFor(host, destination));
        var seen = new List<double>();
        using var subscription = task.Job.ObservableProgress.Subscribe(p => { if (p.HasValue) lock (seen) seen.Add(p.Value.Value); });

        await task;

        lock (seen) seen.Should().NotBeEmpty().And.OnlyContain(v => v >= 0 && v <= 1).And.Contain(v => v > 0.99);
    }

    [Fact]
    public async Task CancellingStopsTheDownloadAndRemovesThePartialFile()
    {
        var plain = Content(100);
        using var started = new SemaphoreSlim(0);
        var host = new FakeHost(async (request, _) =>
        {
            if (request.Uri.Host == "g.api.mega.co.nz") return FakeHost.Json(ApiAnswer(plain.Length));
            started.Release();
            await Task.Delay(TimeSpan.FromSeconds(30));
            return FakeHost.Bytes(Encrypt(plain));
        });
        await using var destination = Files.CreateFile();
        var task = Begin(JobFor(host, destination));

        await started.WaitAsync(TimeSpan.FromSeconds(10));
        task.Job.AsContext().Cancel();
        var act = async () => await task;

        await act.Should().ThrowAsync<OperationCanceledException>();
        destination.Path.FileExists.Should().BeFalse();
    }

    private static SilverModManager.Sdk.Hashes.FileIdentity Hash(byte[] bytes) => Xx64.Of(bytes);

    [Fact(Timeout = 60000)]
    public async Task AnApiAnswerThatGoesQuietAfterItsHeadersFailsNamingMega()
    {
        var host = new FakeHost((_, _) => FakeHost.SilentBody("application/json"));
        await using var destination = Files.CreateFile();

        var act = async () => await Begin(JobFor(host, destination) with { IdleTimeout = TimeSpan.FromMilliseconds(100), Attempts = 2 });

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Be("MEGA stopped sending data");
    }
}
