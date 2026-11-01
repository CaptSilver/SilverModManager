using CliWrap;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Backend.OS;
using SilverModManager.Backend.RuntimeDependency;
using NexusMods.Paths;
using SilverModManager.Sdk;
using NSubstitute;

namespace SilverModManager.CrossPlatform.Tests;

using Xunit;

/// <summary>
/// Registering a link handler from an AppImage that an AppImage manager already integrated. The data home is a
/// temp directory and the process runner is a substitute, so nothing touches the real menu or runs xdg-settings.
/// </summary>
public class RegisterUriSchemeHandlerAppImageTests : IDisposable
{
    private const string OwnDesktopFile = $"{ApplicationConstants.ApplicationId}.desktop";
    private readonly string _dataHome = Directory.CreateTempSubdirectory("register-handler").FullName;
    private readonly string _applications;
    private readonly string _appImage;
    private readonly List<string> _commands = [];
    private readonly List<(LogLevel Level, string Message)> _logs = [];
    private string? _currentDefault;
    private bool _getFails;
    private readonly LinuxInterop _interop;

    public RegisterUriSchemeHandlerAppImageTests()
    {
        _applications = Path.Combine(_dataHome, "applications");
        Directory.CreateDirectory(_applications);
        _appImage = Path.Combine(_dataHome, "AppImages", "silvermodmanager.appimage");

        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<Command>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var command = call.Arg<Command>();
            lock (_commands) _commands.Add($"{command.TargetFilePath} {command.Arguments}");
            if (command.Arguments.StartsWith("get default-url-scheme-handler"))
            {
                if (_getFails) throw new System.ComponentModel.Win32Exception("xdg-settings not found");
                return AnswerGet(command, cancellationToken: call.Arg<CancellationToken>());
            }
            return Task.FromResult<CommandResult>(null!);
        });

        var fileSystem = FileSystem.Shared.CreateOverlayFileSystem(
            new Dictionary<AbsolutePath, AbsolutePath>(),
            new Dictionary<KnownPath, AbsolutePath> { [KnownPath.XDG_DATA_HOME] = FileSystem.Shared.FromUnsanitizedFullPath(_dataHome) });

        var services = new ServiceCollection()
            .AddSingleton<ILogger<LinuxInterop>>(new CapturingLogger(_logs))
            .AddSingleton(runner)
            .AddSingleton<IFileSystem>(fileSystem)
            .AddSingleton(new XdgSettingsDependency(runner))
            .AddSingleton(new UpdateDesktopDatabaseDependency(runner))
            .BuildServiceProvider();
        _interop = new LinuxInterop(services);
    }

    // Stands in for xdg-settings: writes the configured answer to the command's stdout pipe.
    private async Task<CommandResult> AnswerGet(Command command, CancellationToken cancellationToken)
    {
        var answer = _currentDefault is null ? "" : _currentDefault + "\n";
        await command.StandardOutputPipe.CopyFromAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(answer)), cancellationToken);
        return null!;
    }

    private sealed class CapturingLogger(List<(LogLevel, string)> logs) : ILogger<LinuxInterop>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => logs.Add((logLevel, formatter(state, exception)));
    }

    public void Dispose() => Directory.Delete(_dataHome, recursive: true);

    private string Applications(string name) => Path.Combine(_applications, name);

    private void WriteLauncherEntry(string mimeType = "x-scheme-handler/nxm;")
    {
        File.WriteAllText(Applications("silvermodmanager.desktop"), $"""
            [Desktop Entry]
            Type=Application
            Name=SilverModManager
            Exec=env DESKTOPINTEGRATION=1 {_appImage} %u
            MimeType={mimeType}
            """);
    }

    private ValueTask<UriSchemeRegistration> Register(
        string? desktopIntegration,
        InstallationMethod method = InstallationMethod.AppImage,
        string scheme = "nxm",
        UriSchemeClaim claim = UriSchemeClaim.Take)
        => _interop.RegisterUriSchemeHandler(scheme, setAsDefaultHandler: true, method, desktopIntegration, _appImage, CancellationToken.None, claim);

    private bool Ran(string fragment) => _commands.Exists(c => c.Contains(fragment));

    [Fact]
    public async Task AMatchingLauncherEntryBecomesTheDefaultAndNoOwnFileIsWritten()
    {
        WriteLauncherEntry();

        await Register("1");

        File.Exists(Applications(OwnDesktopFile)).Should().BeFalse();
        _commands.Should().Contain(c => c.Contains("update-desktop-database"));
        _commands.Should().Contain(c => c.Contains("xdg-settings") && c.EndsWith("set default-url-scheme-handler nxm silvermodmanager.desktop"));
    }

    [Fact]
    public async Task StaleFilesFromAnEarlierRunAreRemoved()
    {
        WriteLauncherEntry();
        var stale = new[] { OwnDesktopFile, $"{OwnDesktopFile}.bak", $"{ApplicationConstants.ApplicationId}.sh" };
        foreach (var name in stale) File.WriteAllText(Applications(name), "old");

        await Register("1");

        foreach (var name in stale) File.Exists(Applications(name)).Should().BeFalse(name);
        File.Exists(Applications("silvermodmanager.desktop")).Should().BeTrue("the launcher's own entry must be left alone");
    }

    [Fact]
    public async Task WithoutAMatchingEntryTheOwnFileIsWrittenAsBefore()
    {
        await Register("1");

        File.Exists(Applications(OwnDesktopFile)).Should().BeTrue();
        _commands.Should().Contain(c => c.Contains("xdg-settings") && c.EndsWith($"nxm {OwnDesktopFile}"));
    }

    [Fact]
    public async Task AnEntryThatDoesNotAdvertiseTheSchemeIsStillUsedAndTheGapIsLogged()
    {
        WriteLauncherEntry(mimeType: "x-scheme-handler/nxm;");

        await Register("1", scheme: "ror2mm");

        File.Exists(Applications(OwnDesktopFile)).Should().BeFalse("a second menu entry is what the match is there to prevent");
        _commands.Should().Contain(c => c.Contains("xdg-settings") && c.EndsWith("set default-url-scheme-handler ror2mm silvermodmanager.desktop"));
        _logs.Should().Contain(l => l.Level == LogLevel.Warning && l.Message.Contains("silvermodmanager.desktop") && l.Message.Contains("ror2mm"));
    }

    [Fact]
    public async Task AnEntryThatAdvertisesTheSchemeLogsNoWarning()
    {
        WriteLauncherEntry(mimeType: "x-scheme-handler/nxm;x-scheme-handler/ror2mm;");

        await Register("1", scheme: "ror2mm");

        _logs.Should().NotContain(l => l.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task TheCurrentDefaultIsReadFromXdgSettingsForTheScheme()
    {
        await Register("1", scheme: "ror2mm");

        _commands.Should().Contain(c => c == "xdg-settings get default-url-scheme-handler ror2mm");
    }

    [Fact]
    public async Task IfUnclaimedLeavesAnotherHandlerAloneAndWritesNothing()
    {
        _currentDefault = "r2modman.desktop";

        var result = await Register("1", scheme: "ror2mm", claim: UriSchemeClaim.IfUnclaimed);

        result.Should().Be(new UriSchemeRegistration(false, "r2modman.desktop"));
        Directory.GetFiles(_applications).Should().BeEmpty();
        Ran("update-desktop-database").Should().BeFalse();
        Ran("set default-url-scheme-handler").Should().BeFalse();
    }

    [Fact]
    public async Task TakeReplacesAnotherHandlerAndNamesIt()
    {
        _currentDefault = "r2modman.desktop";

        var result = await Register("1", scheme: "ror2mm", claim: UriSchemeClaim.Take);

        result.Should().Be(new UriSchemeRegistration(true, "r2modman.desktop"));
        _commands.Should().Contain(c => c.EndsWith($"set default-url-scheme-handler ror2mm {OwnDesktopFile}"));
        File.Exists(Applications(OwnDesktopFile)).Should().BeTrue();
    }

    [Theory]
    [InlineData("silvermodmanager.desktop")]
    [InlineData(OwnDesktopFile)]
    public async Task OurOwnLauncherEntryOrFileIsNotAnotherHandler(string current)
    {
        WriteLauncherEntry();
        _currentDefault = current;

        var result = await Register("1", claim: UriSchemeClaim.IfUnclaimed);

        result.Should().Be(new UriSchemeRegistration(true, null));
        Ran("set default-url-scheme-handler").Should().BeTrue("the registration is refreshed");
    }

    [Fact]
    public async Task OurLauncherEntryHoldingTheDefaultIsNotAnotherHandlerWithoutTheVariable()
    {
        WriteLauncherEntry();
        _currentDefault = "silvermodmanager.desktop";

        var result = await Register(null, claim: UriSchemeClaim.IfUnclaimed);

        result.Should().Be(new UriSchemeRegistration(true, null));
    }

    [Fact]
    public async Task AnEntryThatRunsADifferentAppImageStillCountsAsAnotherHandler()
    {
        File.WriteAllText(Applications("silvermodmanager.desktop"), $"""
            [Desktop Entry]
            Type=Application
            Name=Other
            Exec=env DESKTOPINTEGRATION=1 {Path.Combine(_dataHome, "AppImages", "other.appimage")} %u
            MimeType=x-scheme-handler/nxm;
            """);
        _currentDefault = "silvermodmanager.desktop";

        var result = await Register(null, claim: UriSchemeClaim.IfUnclaimed);

        result.Should().Be(new UriSchemeRegistration(false, "silvermodmanager.desktop"));
    }

    [Fact]
    public async Task OurOwnFileIsNotAnotherHandlerWhenNoLauncherEntryExists()
    {
        _currentDefault = OwnDesktopFile;

        var result = await Register("1", claim: UriSchemeClaim.IfUnclaimed);

        result.Should().Be(new UriSchemeRegistration(true, null));
        File.Exists(Applications(OwnDesktopFile)).Should().BeTrue();
    }

    [Fact]
    public async Task AnUnsetDefaultRegistersEvenWhenOnlyUnclaimedIsAsked()
    {
        var result = await Register("1", scheme: "ror2mm", claim: UriSchemeClaim.IfUnclaimed);

        result.Should().Be(new UriSchemeRegistration(true, null));
        Ran("set default-url-scheme-handler ror2mm").Should().BeTrue();
    }

    [Fact]
    public async Task AMissingXdgSettingsCountsAsUnset()
    {
        _getFails = true;

        var result = await Register("1", claim: UriSchemeClaim.IfUnclaimed);

        result.Should().Be(new UriSchemeRegistration(true, null));
        File.Exists(Applications(OwnDesktopFile)).Should().BeTrue();
    }

    [Theory]
    [InlineData(InstallationMethod.Flatpak)]
    [InlineData(InstallationMethod.PackageManager)]
    public async Task InstallsThatSkipTheDefaultNeverAskForIt(InstallationMethod method)
    {
        _currentDefault = "r2modman.desktop";

        var result = await Register(null, method, claim: UriSchemeClaim.IfUnclaimed);

        result.Should().Be(new UriSchemeRegistration(true, null));
        Ran("xdg-settings").Should().BeFalse();
    }

    [Fact]
    public async Task TheWrittenDesktopFileListsBothSchemes()
    {
        await Register(null, InstallationMethod.Manually, "ror2mm");

        File.ReadAllLines(Applications(OwnDesktopFile)).Should().Contain("MimeType=x-scheme-handler/nxm;x-scheme-handler/ror2mm;");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task WithoutTheVariableAMatchingEntryIsIgnored(string? value)
    {
        WriteLauncherEntry();

        await Register(value);

        File.Exists(Applications(OwnDesktopFile)).Should().BeTrue();
        _commands.Should().Contain(c => c.Contains("xdg-settings") && c.EndsWith($"nxm {OwnDesktopFile}"));
    }

    [Fact]
    public async Task ManualInstallsKeepWritingTheirOwnFile()
    {
        WriteLauncherEntry();

        await Register("1", InstallationMethod.Manually);

        File.Exists(Applications(OwnDesktopFile)).Should().BeTrue();
    }
}
