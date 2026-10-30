using System.Collections.Concurrent;
using System.Net;
using System.Web;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Backend.Jobs;
using SilverModManager.Networking.Forums.Auth;
using SilverModManager.Networking.Forums.Downloads;
using SilverModManager.Sdk;

namespace SilverModManager.Networking.Forums.Tests;

/// <summary>
/// Runs a whole sign-in, a refresh and a files lookup against a fake forum with every logger at Trace, then looks
/// for anything that must never be written down.
/// </summary>
public class NoSecretsInLogsTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly CapturingLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggers;
    private readonly FakeTimeProvider _time = new(Start);
    private readonly StubHandler _forum = new();
    private readonly InMemorySecretStore _store = new();
    private readonly ForumCredentials _credentials;
    private readonly JobMonitor _monitor;

    public NoSecretsInLogsTests()
    {
        _loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(_logs));
        _credentials = new ForumCredentials(_store, _loggers);
        _monitor = new JobMonitor(_loggers.CreateLogger<JobMonitor>());
    }

    public void Dispose()
    {
        _credentials.Dispose();
        _monitor.Dispose();
        _loggers.Dispose();
    }

    [Fact]
    public async Task NothingSecretReachesAnyLogLine()
    {
        const string accessToken = "ACCESS-TOKEN-1f3a9";
        const string refreshToken = "REFRESH-TOKEN-77c2e";
        const string refreshedAccess = "REFRESHED-ACCESS-b40d";
        const string code = "AUTH-CODE-5e81";

        var os = Substitute.For<IOSInterop>();
        var opened = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        os.When(o => o.OpenUri(Arg.Any<Uri>())).Do(call => opened.TrySetResult(call.Arg<Uri>()));

        _forum.ReplyWhen(r => r.Uri == ForumEndpoints.For(ForumSite.LoversLab).Token && r.Body.Contains("authorization_code"), HttpStatusCode.OK,
            $$"""{"access_token":"{{accessToken}}","refresh_token":"{{refreshToken}}","expires_in":3600}""", Task.CompletedTask);
        _forum.ReplyWhen(r => r.Uri == ForumEndpoints.For(ForumSite.LoversLab).Token, HttpStatusCode.OK,
            $$"""{"access_token":"{{refreshedAccess}}","refresh_token":"{{refreshToken}}","expires_in":3600}""", Task.CompletedTask);
        _forum.Reply(HttpStatusCode.OK, """{"files":[{"name":"mod.7z","url":"https://files.test/get/abc","size":10}]}""");

        var oauth = new ForumOAuth(new HttpClient(_forum), _credentials, site => ForumEndpoints.For(site) with { ClientId = "client" }, _time, _loggers.CreateLogger<ForumOAuth>());
        var manager = new ForumLoginManager(oauth, _credentials, _time, _monitor, os, _loggers.CreateLogger<ForumLoginManager>());
        var files = new ForumFilesClient(new HttpClient(_forum), oauth, _credentials, _time, _loggers.CreateLogger<ForumFilesClient>());
        await _credentials.PrimeAsync(CancellationToken.None);

        // Sign in through the real listener.
        var signIn = manager.SignIn(ForumSite.LoversLab, CancellationToken.None);
        var authorize = await opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var query = HttpUtility.ParseQueryString(authorize.Query);
        var state = query["state"]!;
        var challenge = query["code_challenge"]!;
        using (var browser = new HttpClient())
        {
            using var _ = await browser.GetAsync(new Uri($"{query["redirect_uri"]}?code={code}&state={Uri.EscapeDataString(state)}"));
        }
        await signIn.WaitAsync(TimeSpan.FromSeconds(10));
        var exchangeBody = _forum.Requests.Single().Body;
        var verifier = HttpUtility.ParseQueryString(exchangeBody)["code_verifier"]!;

        // Let the token lapse so the lookup refreshes it, then look a file up.
        _time.Advance(TimeSpan.FromHours(2));
        var url = await files.Resolve(ForumSite.LoversLab, 42, "mod.7z", 10, CancellationToken.None);
        url.Should().NotBeNull();
        _forum.Requests.Count(r => r.Method == HttpMethod.Post).Should().Be(2, "the sign-in and the refresh both reached the token endpoint");
        _forum.Requests.Last().Authorization.Should().Be($"Bearer {refreshedAccess}");

        var tokenBodies = _forum.Requests.Where(r => r.Method == HttpMethod.Post).Select(r => r.Body).ToArray();
        var secrets = new[] { code, verifier, challenge, state, accessToken, refreshToken, refreshedAccess, "Bearer", authorize.Query.TrimStart('?'), "/callback?" }
            .Concat(tokenBodies).ToArray();

        _logs.Lines.Should().NotBeEmpty("the run should have logged something for this test to mean anything");
        foreach (var secret in secrets)
            _logs.Lines.Should().NotContain(line => line.Contains(secret, StringComparison.Ordinal), $"'{secret}' is a secret");
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyCollection<string> Lines => _lines.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _lines);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string category, ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => lines.Enqueue($"{category}: {formatter(state, exception)}{(exception is null ? "" : " " + exception)}");
        }
    }
}
