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
    private readonly List<OpenUriFailure> _failures = [];
    private readonly RecordingLogger _logger = new();

    private LinuxInterop CreateInterop(Func<Uri, Task<ResponseStatus>> portal, Func<Command, Task<CommandResult>> runner, bool gameMode = false)
    {
        var detector = Substitute.For<IGameModeDetector>();
        detector.IsGameMode.Returns(gameMode);

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
            .AddSingleton(detector)
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

        _failures.Should().Equal(new OpenUriFailure(Link, "No browser could be started."));
        _logger.Entries.Should().Contain(e => e.Message.Contains(Link.AbsoluteUri) && e.Message.Contains("xdg-open"));
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Message.StartsWith("xdg-open"), "the link did not open at all");
    }

    // The app shows an Error-level log entry as a dialog, so a portal failure the fallback recovers from must not be one.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task APortalFailureThatXdgOpenRecoversFrom_IsAWarningNotAnError(bool portalThrows)
    {
        var interop = CreateInterop(
            _ => portalThrows ? throw new InvalidOperationException("portal is down") : Task.FromResult(ResponseStatus.Aborted),
            _ => Exit(0));

        await interop.OpenUriAsync(Link);

        _failures.Should().BeEmpty();
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("trying xdg-open"));
        _logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task PortalFailsAndXdgOpenCannotStart_ReportsTheUri()
    {
        var interop = CreateInterop(_ => throw new InvalidOperationException("portal is down"), _ => throw new System.ComponentModel.Win32Exception("No such file or directory"));

        await interop.OpenUriAsync(Link);

        _failures.Should().Equal(new OpenUriFailure(Link, "No browser could be started."));
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

        _failures.Should().Equal(new OpenUriFailure(Link, "No browser could be started."));
    }

    [Fact]
    public async Task UserCancelsThePortalChooser_NeitherRunsXdgOpenNorReportsAFailure()
    {
        var interop = CreateInterop(_ => Task.FromResult(ResponseStatus.UserCancelled), _ => Exit(0));

        await interop.OpenUriAsync(Link);

        _commands.Should().BeEmpty();
        _failures.Should().BeEmpty();
    }

    [Fact]
    public async Task InGameModeALinkSkipsThePortalAndXdgOpenAndFailsWithTheGameModeReason()
    {
        var portalCalls = 0;
        var interop = CreateInterop(_ => { portalCalls++; return Task.FromResult(ResponseStatus.Success); }, _ => Exit(0), gameMode: true);

        await interop.OpenUriAsync(Link);

        portalCalls.Should().Be(0);
        _commands.Should().BeEmpty();
        _failures.Should().Equal(new OpenUriFailure(Link, "There is no browser in Game Mode."));
    }

    [Fact]
    public async Task InGameModeASteamLinkStillGoesThroughThePortal()
    {
        var portalCalls = 0;
        var interop = CreateInterop(_ => { portalCalls++; return Task.FromResult(ResponseStatus.Success); }, _ => Exit(0), gameMode: true);

        await interop.OpenUriAsync(new Uri("steam://run/1"));

        portalCalls.Should().Be(1);
        _failures.Should().BeEmpty();
    }

    [Fact]
    public void InGameModeOpenDirectoryRaisesAFileUriFailure()
    {
        var interop = CreateInterop(_ => Task.FromResult(ResponseStatus.Success), _ => Exit(0), gameMode: true);
        var dir = Directory.CreateTempSubdirectory("silvermod-openuri-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "a.txt"), "x");

            interop.OpenDirectory(FileSystem.Shared.FromUnsanitizedFullPath(dir.FullName));

            var failure = _failures.Should().ContainSingle().Subject;
            failure.Uri.Should().Be(new Uri(dir.FullName));
            failure.Uri.IsFile.Should().BeTrue();
            failure.Reason.Should().Be("There is no file manager in Game Mode.");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void InGameModeAPathWithUriSpecialCharactersComesBackUnchanged()
    {
        var interop = CreateInterop(_ => Task.FromResult(ResponseStatus.Success), _ => Exit(0), gameMode: true);
        var dir = Directory.CreateTempSubdirectory("silvermod-openuri-");
        try
        {
            var odd = Directory.CreateDirectory(Path.Combine(dir.FullName, "Mod #2 100%41 ?x"));
            File.WriteAllText(Path.Combine(odd.FullName, "a.txt"), "x");

            interop.OpenDirectory(FileSystem.Shared.FromUnsanitizedFullPath(odd.FullName));

            _failures.Should().ContainSingle().Which.Uri.LocalPath.Should().Be(odd.FullName);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void InGameModeOpenFileAndOpenFileInDirectoryRaiseAFileUriFailure()
    {
        var interop = CreateInterop(_ => Task.FromResult(ResponseStatus.Success), _ => Exit(0), gameMode: true);
        var dir = Directory.CreateTempSubdirectory("silvermod-openuri-");
        try
        {
            var file = Path.Combine(dir.FullName, "a.txt");
            File.WriteAllText(file, "x");
            var path = FileSystem.Shared.FromUnsanitizedFullPath(file);

            interop.OpenFile(path);
            interop.OpenFileInDirectory(path);

            _failures.Should().HaveCount(2).And.OnlyContain(f => f.Uri == new Uri(file) && f.Reason == "There is no file manager in Game Mode.");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
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
