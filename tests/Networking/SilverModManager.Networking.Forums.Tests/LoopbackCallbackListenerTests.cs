using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using SilverModManager.Networking.Forums.Auth;

namespace SilverModManager.Networking.Forums.Tests;

public class LoopbackCallbackListenerTests : IDisposable
{
    private const string State = "state-abc";
    private const string Site = "LoversLab";
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);

    private readonly LoopbackCallbackListener _listener = new();
    private readonly HttpClient _http = new();

    public void Dispose()
    {
        _http.Dispose();
        _listener.Dispose();
    }

    private Task<CallbackResult> Wait(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => _listener.WaitAsync(State, Site, timeout ?? Long, cancellationToken);

    private Task<HttpResponseMessage> Get(string pathAndQuery)
        => _http.GetAsync(new Uri(_listener.RedirectUri, pathAndQuery));

    private async Task<string> RawExchange(string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _listener.RedirectUri.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        using var reader = new StreamReader(stream, Encoding.ASCII);
        return await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void BindsTheLoopbackAddressOnAnEphemeralPort()
    {
        _listener.RedirectUri.Scheme.Should().Be("http");
        _listener.RedirectUri.Host.Should().Be("127.0.0.1");
        _listener.RedirectUri.Port.Should().BeGreaterThan(0);
        _listener.RedirectUri.AbsolutePath.Should().Be("/callback");
    }

    [Fact]
    public async Task ACallbackWithTheRightStateReturnsTheCodeAndAScriptFreePage()
    {
        var wait = Wait();

        var response = await Get($"/callback?code=the-code&state={State}");

        (await wait.WaitAsync(Long)).Code.Should().Be("the-code");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Signed in to LoversLab").And.Contain("close this tab");
        body.Should().NotContainEquivalentOf("<script");
    }

    [Fact]
    public async Task ThePercentEncodedCodeIsDecoded()
    {
        var wait = Wait();

        await Get($"/callback?state={State}&code=a%2Bb%3D%3D%20c");

        (await wait.WaitAsync(Long)).Code.Should().Be("a+b== c");
    }

    [Theory]
    [InlineData("/callback?code=the-code&state=someone-elses")]
    [InlineData("/callback?code=the-code")]
    [InlineData("/callback?state=&code=the-code")]
    public async Task AWrongOrMissingStateIsRefusedAndTheSlotIsNotUsedUp(string pathAndQuery)
    {
        var wait = Wait();

        var response = await Get(pathAndQuery);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        wait.IsCompleted.Should().BeFalse();

        await Get($"/callback?code=real-code&state={State}");
        (await wait.WaitAsync(Long)).Code.Should().Be("real-code");
    }

    [Fact]
    public async Task AnErrorCallbackWithTheWrongStateDoesNotEndTheWait()
    {
        var wait = Wait();

        var response = await Get("/callback?error=access_denied&state=forged");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        wait.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task AMatchingStateWithNeitherCodeNorErrorIsRefused()
    {
        var wait = Wait();

        var response = await Get($"/callback?state={State}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        wait.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task TheFaviconRequestGetsA404AndTheRealCallbackStillCompletes()
    {
        var wait = Wait();

        var favicon = await Get("/favicon.ico");
        favicon.StatusCode.Should().Be(HttpStatusCode.NotFound);
        wait.IsCompleted.Should().BeFalse();

        await Get($"/callback?code=the-code&state={State}");
        (await wait.WaitAsync(Long)).Code.Should().Be("the-code");
    }

    [Fact]
    public async Task ClickingDenyFailsAtOnceAsDeclined()
    {
        var wait = Wait();

        var response = await Get($"/callback?error=access_denied&error_description=nope&state={State}");

        var failure = await wait.Invoking(w => w.WaitAsync(Long)).Should().ThrowAsync<SignInDeclinedException>();
        failure.Which.Message.Should().Be("Sign-in to LoversLab was declined");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("declined").And.NotContainEquivalentOf("<script");
    }

    [Fact]
    public async Task AnyOtherErrorIsAlsoDeclined()
    {
        var wait = Wait();

        await Get($"/callback?error=server_error&state={State}");

        await wait.Invoking(w => w.WaitAsync(Long)).Should().ThrowAsync<SignInDeclinedException>();
    }

    [Fact]
    public async Task AMalformedRequestGetsA400AndTheWaitContinues()
    {
        var wait = Wait();

        var reply = await RawExchange("this is not http\r\n\r\n");

        reply.Should().StartWith("HTTP/1.1 400");
        wait.IsCompleted.Should().BeFalse();
        await Get($"/callback?code=the-code&state={State}");
        (await wait.WaitAsync(Long)).Code.Should().Be("the-code");
    }

    [Fact]
    public async Task ARequestAddressedToAnotherHostIsRefused()
    {
        var wait = Wait();

        var reply = await RawExchange($"GET /callback?code=c&state={State} HTTP/1.1\r\nHost: evil.example\r\n\r\n");

        reply.Should().StartWith("HTTP/1.1 400");
        wait.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task ARequestWithAnotherMethodIsRefused()
    {
        var wait = Wait();
        var host = $"127.0.0.1:{_listener.RedirectUri.Port}";

        var reply = await RawExchange($"POST /callback?code=c&state={State} HTTP/1.1\r\nHost: {host}\r\nContent-Length: 0\r\n\r\n");

        reply.Should().StartWith("HTTP/1.1 400");
        wait.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task AConnectionThatSendsNothingDoesNotHoldUpTheCallback()
    {
        using var listener = new LoopbackCallbackListener(requestTimeout: TimeSpan.FromMilliseconds(200));
        var wait = listener.WaitAsync(State, Site, Long, CancellationToken.None);
        using var silent = new TcpClient();
        await silent.ConnectAsync(IPAddress.Loopback, listener.RedirectUri.Port);

        var response = await _http.GetAsync(new Uri(listener.RedirectUri, $"/callback?code=the-code&state={State}"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await wait.WaitAsync(Long)).Code.Should().Be("the-code");
    }

    [Fact]
    public async Task AConnectionAfterCompletionIsRefused()
    {
        var wait = Wait();
        await Get($"/callback?code=the-code&state={State}");
        await wait.WaitAsync(Long);

        using var late = new TcpClient();
        var connect = async () => await late.ConnectAsync(IPAddress.Loopback, _listener.RedirectUri.Port);

        await connect.Should().ThrowAsync<SocketException>();
    }

    [Fact]
    public async Task NoCallbackInTimeFailsAsTimedOut()
    {
        var wait = Wait(TimeSpan.FromMilliseconds(150));

        var failure = await wait.Invoking(w => w.WaitAsync(Long)).Should().ThrowAsync<SignInTimedOutException>();

        failure.Which.Message.Should().Be("Sign-in to LoversLab timed out");
    }

    [Fact]
    public async Task CancellingStopsTheWaitAndClosesThePort()
    {
        using var cts = new CancellationTokenSource();
        var wait = Wait(cancellationToken: cts.Token);

        await cts.CancelAsync();

        await wait.Invoking(w => w.WaitAsync(Long)).Should().ThrowAsync<OperationCanceledException>();
        using var late = new TcpClient();
        var connect = async () => await late.ConnectAsync(IPAddress.Loopback, _listener.RedirectUri.Port);
        await connect.Should().ThrowAsync<SocketException>();
    }

    [Fact]
    public async Task ClosingTheListenerUnderAWaitCancelsItInsteadOfFaultingIt()
    {
        // A faulted wait that nobody awaits is reported as an unobserved task exception; a cancelled one is not.
        var wait = Wait();

        _listener.Dispose();

        await wait.Invoking(w => w.WaitAsync(Long)).Should().ThrowAsync<OperationCanceledException>();
        wait.IsCanceled.Should().BeTrue();
    }

    [Fact]
    public async Task NothingFromTheQueryReachesTheLog()
    {
        var logger = new CapturingLogger();
        using var listener = new LoopbackCallbackListener(logger: logger);
        var wait = listener.WaitAsync(State, Site, Long, CancellationToken.None);
        var uri = (string path) => new Uri(listener.RedirectUri, path);

        await _http.GetAsync(uri("/callback?code=wrong&state=forged-state-value"));
        await _http.GetAsync(uri("/favicon.ico?secret=lookup-me"));
        await _http.GetAsync(uri($"/callback?code=secret-code-value&state={State}"));
        await wait.WaitAsync(Long);

        logger.Lines.Should().NotBeEmpty();
        logger.Lines.Should().AllSatisfy(line => line
            .Should().NotContain("secret-code-value").And.NotContain("forged-state-value")
            .And.NotContain(State).And.NotContain("lookup-me"));
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get { lock (_lines) return _lines.ToArray(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_lines) _lines.Add(formatter(state, exception));
        }
    }
}
