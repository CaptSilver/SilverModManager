using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using SilverModManager.Abstractions.Library;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Acquire.Hosts;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Wabbajack.Tests.Acquire.Hosts;

public class HeaderedHttpDownloadJobTests(IServiceProvider provider, TemporaryFileManager files) : HostedJobTestBase(provider, files)
{
    private static readonly Uri FileUrl = new("https://files.test/mods/a.7z");

    private HeaderedHttpDownloadJob JobFor(FakeHost host, AbsolutePath destination, string[] headers, HostThrottle? throttle = null, Uri? url = null) => new()
    {
        Client = new HttpClient(host),
        Logger = NullLogger<HeaderedHttpDownloadJob>.Instance,
        Throttle = throttle ?? new HostThrottle(),
        Url = url ?? FileUrl,
        Headers = headers,
        Destination = destination,
    };

    [Fact]
    public async Task TheListsHeadersAreSentAndTheFileIsAddedToTheLibrary()
    {
        var plain = Content(20_000);
        var host = new FakeHost((_, _) => FakeHost.Bytes(plain));
        await using var destination = Files.CreateFile();

        var file = await Provider.GetRequiredService<ILibraryService>().AddDownload(Begin(JobFor(host, destination, ["Referer: https://files.test/page", "User-Agent: Wabbajack", "Accept: */*"])), UnassignedAcquisition.Instance);

        var sent = host.Seen.Should().ContainSingle().Subject;
        sent.Headers["Referer"].Should().Be("https://files.test/page");
        sent.Headers["User-Agent"].Should().Be("Wabbajack");
        sent.Headers["Accept"].Should().Be("*/*");
        file.XxHash64Identity().Should().Be(Xx64.Of(plain));
    }

    [Theory]
    [InlineData("Cookie: session=abc")]
    [InlineData("Authorization: Bearer secret")]
    [InlineData("authorization: Basic eA==")]
    [InlineData("Proxy-Authorization: x")]
    [InlineData("Host: other.test")]
    [InlineData("X-Api-Key: abc")]
    public async Task AHeaderThatCouldCarryACredentialIsRefusedAndNothingIsSent(string header)
    {
        var host = new FakeHost((_, _) => FakeHost.Bytes(Content(10)));
        await using var destination = Files.CreateFile();

        var act = async () => await Begin(JobFor(host, destination, ["Accept: */*", header]));

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain(header.Split(':')[0], Exactly.Once());
        host.Seen.Should().BeEmpty();
    }

    [Fact(Timeout = 60000)]
    public async Task ABodyThatGoesQuiet_FailsNamingTheHostAndLeavesNoPartialFile()
    {
        var host = new FakeHost((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = StallingContent.After(Content(50_000)) });
        await using var destination = Files.CreateFile();

        var act = async () => await Begin(JobFor(host, destination, ["Accept: */*"]) with { IdleTimeout = TimeSpan.FromMilliseconds(150) });

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Be("files.test stopped sending data");
        destination.Path.FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task AWebPageInsteadOfTheFileFailsAskingForTheBrowser()
    {
        var host = new FakeHost((_, _) => FakeHost.Html("<html>Log in</html>"));
        await using var destination = Files.CreateFile();

        var act = async () => await Begin(JobFor(host, destination, ["Accept: */*"]));

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("web page").And.Contain("browser");
        destination.Path.FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task AForbiddenAnswerNamesTheStatus()
    {
        var host = new FakeHost((_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden));
        await using var destination = Files.CreateFile();

        var act = async () => await Begin(JobFor(host, destination, []));

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("403");
    }

    [Fact]
    public async Task ThreeJobsForOneHostRunTwoAtATime()
    {
        var host = new FakeHost(async (_, _) =>
        {
            await Task.Delay(100);
            return FakeHost.Bytes(Content(100));
        });
        var throttle = new HostThrottle();
        var destinations = Enumerable.Range(0, 3).Select(_ => Files.CreateFile()).ToArray();

        await Task.WhenAll(destinations.Select(async d => await Begin(JobFor(host, d, [], throttle))));

        host.MaxInFlight.Should().Be(2);
        foreach (var d in destinations) await d.DisposeAsync();
    }

    [Fact]
    public async Task JobsForDifferentHostsDoNotShareSlots()
    {
        var host = new FakeHost(async (_, _) =>
        {
            await Task.Delay(100);
            return FakeHost.Bytes(Content(100));
        });
        var throttle = new HostThrottle();
        var destinations = Enumerable.Range(0, 4).Select(_ => Files.CreateFile()).ToArray();

        await Task.WhenAll(destinations.Select(async (d, i) => await Begin(JobFor(host, d, [], throttle, new Uri($"https://host{i}.test/a.7z")))));

        host.MaxInFlight.Should().Be(4);
        foreach (var d in destinations) await d.DisposeAsync();
    }

    [Theory]
    [InlineData("Referer: https://a.test/x?y=1", "Referer", "https://a.test/x?y=1")]
    [InlineData("  accept :  text/html ", "accept", "text/html")]
    public void AHeaderLineSplitsOnItsFirstColon(string line, string name, string value)
    {
        HeaderPolicy.TryParse(line, out var parsedName, out var parsedValue).Should().BeTrue();

        parsedName.Should().Be(name);
        parsedValue.Should().Be(value);
    }

    [Theory]
    [InlineData("no colon")]
    [InlineData(": value")]
    [InlineData("")]
    public void ALineThatIsNotAHeaderDoesNotParse(string line)
    {
        HeaderPolicy.TryParse(line, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void OnlyListsWhoseHeadersAreAllSafeCanBeSent()
    {
        HeaderPolicy.CanSendAll(["Referer: x", "User-Agent: y"]).Should().BeTrue();
        HeaderPolicy.CanSendAll([]).Should().BeTrue();
        HeaderPolicy.CanSendAll(["Referer: x", "Cookie: y"]).Should().BeFalse();
        HeaderPolicy.CanSendAll(["garbled"]).Should().BeFalse();
    }
}
