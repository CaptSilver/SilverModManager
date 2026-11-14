using FluentAssertions;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Platform.Proton.Steam;
using SilverModManager.Sdk;

namespace SilverModManager.Platform.Proton.Tests.Steam;

public class LauncherShimRefresherTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("silvermod-refresher-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class CapturingLogger : ILogger<LauncherShimRefresher>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private (CapturingLogger Logger, string Shim) Refresh(string target)
    {
        var interop = Substitute.For<IOSInterop>();
        interop.GetRunningExecutablePath(out _).Returns(call => { call[0] = target; return FileSystem.Shared.FromUnsanitizedFullPath(target); });
        var environment = Substitute.For<IEnvironmentReader>();
        environment.Get("XDG_DATA_HOME").Returns(System.IO.Path.Combine(_root, "data"));
        var logger = new CapturingLogger();

        new LauncherShimRefresher(interop, environment, FileSystem.Shared, logger).StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        return (logger, System.IO.Path.Combine(_root, "data", "silvermod", "bin", "silvermod"));
    }

    [Theory]
    [InlineData("/work/src/App/bin/Debug/net10.0/SilverModManager.App")]
    [InlineData("/work/src/App/bin/Release/net10.0/SilverModManager.App")]
    public void TheRefresherSkipsABinDebugOrReleaseTarget(string target)
    {
        var (logger, shim) = Refresh(target);

        File.Exists(shim).Should().BeFalse();
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains(target));
    }

    [Fact]
    public void AnInstalledTargetIsWritten()
    {
        var (logger, shim) = Refresh("/home/u/AppImages/SilverModManager.AppImage");

        File.ReadAllText(shim).Should().Contain("/home/u/AppImages/SilverModManager.AppImage");
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public void AFolderNamedBinDebugAsAPrefixOfAnotherNameIsNotSkipped()
    {
        var (_, shim) = Refresh("/home/u/bin/Debugger/app");

        File.Exists(shim).Should().BeTrue();
    }
}
