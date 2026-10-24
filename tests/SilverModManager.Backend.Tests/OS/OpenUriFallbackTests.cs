using CliWrap;
using FluentAssertions;
using LinuxDesktopUtils.XDGDesktopPortal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Backend.OS;
using SilverModManager.Backend.RuntimeDependency;
using SilverModManager.Sdk;

namespace SilverModManager.Backend.Tests.OS;

/// <summary>
/// Opening a link when the desktop portal call fails. The portal and the process runner are fakes, so no browser
/// is started and no D-Bus connection is made.
/// </summary>
public class OpenUriFallbackTests
{
    private static readonly Uri Link = new("https://example.com/releases/1");

    private readonly List<string> _commands = [];
    private readonly List<Uri> _failures = [];
    private readonly RecordingLogger _logger = new();

    private LinuxInterop CreateInterop(Func<Uri, Task<ResponseStatus>> portal, Func<Command, Task<CommandResult>> runner)
    {
        var processRunner = Substitute.For<IProcessRunner>();
        processRunner.RunAsync(Arg.Any<Command>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var command = call.Arg<Command>();
            _commands.Add($"{command.TargetFilePath} {command.Arguments}");
            return runner(command);
        });

        var services = new ServiceCollection()
            .AddSingleton<ILogger<LinuxInterop>>(_logger)
            .AddSingleton(processRunner)
            .AddSingleton<IFileSystem>(FileSystem.Shared)
            .AddSingleton(new XdgSettingsDependency(processRunner))
            .AddSingleton(new UpdateDesktopDatabaseDependency(processRunner))
            .BuildServiceProvider();

        var interop = new LinuxInterop(services, portal);
        interop.OpenUriFailed += _failures.Add;
        return interop;
    }

    private static Task<CommandResult> Exit(int code) => Task.FromResult(new CommandResult(code, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

    [Fact]
    public async Task PortalSucceeds_DoesNotRunXdgOpen()
    {
        var interop = CreateInterop(_ => Task.FromResult(ResponseStatus.Success), _ => Exit(0));

        await interop.OpenUriAsync(Link);

        _commands.Should().BeEmpty();
        _failures.Should().BeEmpty();
    }

    [Fact]
    public async Task PortalThrows_LogsTheErrorWithTheUri_AndFallsBackToXdgOpen()
    {
        var interop = CreateInterop(_ => throw new InvalidOperationException("portal is down"), _ => Exit(0));

        await interop.OpenUriAsync(Link);

        _commands.Should().ContainSingle().Which.Should().Be($"xdg-open {Link.AbsoluteUri}");
        _failures.Should().BeEmpty();
        _logger.Entries.Should().Contain(e => e.Exception != null && e.Exception.Message == "portal is down" && e.Message.Contains(Link.AbsoluteUri));
    }

    [Fact]
    public async Task PortalAndXdgOpenFail_ReportsTheUri()
    {
        var interop = CreateInterop(_ => throw new InvalidOperationException("portal is down"), _ => Exit(3));

        await interop.OpenUriAsync(Link);

        _failures.Should().Equal(Link);
        _logger.Entries.Should().Contain(e => e.Message.Contains(Link.AbsoluteUri) && e.Message.Contains("xdg-open"));
    }

    [Fact]
    public async Task PortalFailsAndXdgOpenCannotStart_ReportsTheUri()
    {
        var interop = CreateInterop(_ => throw new InvalidOperationException("portal is down"), _ => throw new System.ComponentModel.Win32Exception("No such file or directory"));

        await interop.OpenUriAsync(Link);

        _failures.Should().Equal(Link);
    }

    [Fact]
    public async Task PortalFailureLog_NamesTheSessionBusEnvironment()
    {
        var interop = CreateInterop(_ => throw new InvalidOperationException("portal is down"), _ => Exit(0));

        await interop.OpenUriAsync(Link);

        _logger.Entries.Should().Contain(e => e.Message.Contains("DBUS_SESSION_BUS_ADDRESS") && e.Message.Contains("APPIMAGE"));
    }

    [Fact]
    public async Task PortalAborts_FallsBackToXdgOpen()
    {
        var interop = CreateInterop(_ => Task.FromResult(ResponseStatus.Aborted), _ => Exit(0));

        await interop.OpenUriAsync(Link);

        _commands.Should().ContainSingle().Which.Should().Be($"xdg-open {Link.AbsoluteUri}");
        _failures.Should().BeEmpty();
        _logger.Entries.Should().Contain(e => e.Message.Contains(Link.AbsoluteUri) && e.Message.Contains(nameof(ResponseStatus.Aborted)));
    }

    [Fact]
    public async Task PortalAbortsAndXdgOpenFails_ReportsTheUri()
    {
        var interop = CreateInterop(_ => Task.FromResult(ResponseStatus.Aborted), _ => Exit(3));

        await interop.OpenUriAsync(Link);

        _failures.Should().Equal(Link);
    }

    [Fact]
    public async Task UserCancelsThePortalChooser_NeitherRunsXdgOpenNorReportsAFailure()
    {
        var interop = CreateInterop(_ => Task.FromResult(ResponseStatus.UserCancelled), _ => Exit(0));

        await interop.OpenUriAsync(Link);

        _commands.Should().BeEmpty();
        _failures.Should().BeEmpty();
    }

    private sealed class RecordingLogger : ILogger<LinuxInterop>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
