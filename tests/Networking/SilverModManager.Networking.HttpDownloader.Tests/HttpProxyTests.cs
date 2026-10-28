using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace SilverModManager.Networking.HttpDownloader.Tests;

[CollectionDefinition(HttpProxyTests.Name, DisableParallelization = true)]
public class HttpProxyCollection;

/// <remarks>
/// Applying a proxy replaces <see cref="HttpClient.DefaultProxy"/> for the whole process, so these don't run beside other tests.
/// </remarks>
[Collection(Name)]
public class HttpProxyTests
{
    public const string Name = "HttpProxy";

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8888", "http://127.0.0.1:8888/")]
    [InlineData("  https://proxy.example:3128  ", "https://proxy.example:3128/")]
    [InlineData("socks5://127.0.0.1:1080", "socks5://127.0.0.1:1080/")]
    public void AValidProxyUrl_IsAccepted(string value, string expected)
    {
        var logger = new RecordingLogger();

        var uri = HttpProxy.Parse(value, logger);

        uri.Should().Be(new Uri(expected));
        logger.Entries.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyValue_MeansNoProxyAndIsNotWorthAWarning(string? value)
    {
        var logger = new RecordingLogger();

        HttpProxy.Parse(value, logger).Should().BeNull();
        logger.Entries.Should().BeEmpty();
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("127.0.0.1:8888")]
    [InlineData("ftp://proxy.example:21")]
    [InlineData("file:///tmp/proxy")]
    public void AnInvalidValue_IsRejectedWithAWarningNamingIt(string value)
    {
        var logger = new RecordingLogger();

        HttpProxy.Parse(value, logger).Should().BeNull();

        logger.Entries.Should().ContainSingle().Which.Should().Match<(LogLevel Level, string Message)>(e => e.Level == LogLevel.Warning && e.Message.Contains(value));
    }

    [Fact]
    public void AProxyWithCredentialsInTheUrl_IsRefusedWithoutEchoingThePassword()
    {
        var logger = new RecordingLogger();

        HttpProxy.Parse("http://user:hunter2@127.0.0.1:8888", logger).Should().BeNull();

        var entry = logger.Entries.Should().ContainSingle().Which;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should().Contain("user name and password").And.NotContain("hunter2").And.NotContain("user:");
    }

    [Fact]
    public void TheDescriptionPointsAtTheProxyEnvironmentVariables()
    {
        var description = HttpProxySettings.Description;

        description.Should().Contain("http_proxy").And.Contain("https_proxy").And.Contain("all_proxy");
    }

    [Fact]
    public void ApplyingAProxy_SetsTheDefaultProxyThatEveryHandlerUses()
    {
        var original = HttpClient.DefaultProxy;
        try
        {
            HttpProxy.Apply(new HttpProxySettings { HttpProxy = "http://127.0.0.1:8888" }, new RecordingLogger());

            var proxy = HttpClient.DefaultProxy;
            proxy.GetProxy(new Uri("https://premium-files.nexus-cdn.com/file")).Should().Be(new Uri("http://127.0.0.1:8888"));
            // A local server is not something a VPN's proxy can reach
            proxy.IsBypassed(new Uri("http://127.0.0.1:5000/file")).Should().BeTrue();
        }
        finally
        {
            HttpClient.DefaultProxy = original;
        }
    }

    [Fact]
    public void ApplyingAnEmptySetting_LeavesTheDefaultProxyAlone()
    {
        var original = HttpClient.DefaultProxy;
        var marker = new WebProxy("http://marker.invalid:1");
        try
        {
            HttpClient.DefaultProxy = marker;
            HttpProxy.Apply(new HttpProxySettings(), new RecordingLogger());
            HttpClient.DefaultProxy.Should().BeSameAs(marker);
        }
        finally
        {
            HttpClient.DefaultProxy = original;
        }
    }

    [Fact]
    public void ApplyingAnInvalidSetting_LeavesTheDefaultProxyAlone()
    {
        var original = HttpClient.DefaultProxy;
        var marker = new WebProxy("http://marker.invalid:1");
        try
        {
            HttpClient.DefaultProxy = marker;
            HttpProxy.Apply(new HttpProxySettings { HttpProxy = "garbage" }, new RecordingLogger());
            HttpClient.DefaultProxy.Should().BeSameAs(marker);
        }
        finally
        {
            HttpClient.DefaultProxy = original;
        }
    }
}
