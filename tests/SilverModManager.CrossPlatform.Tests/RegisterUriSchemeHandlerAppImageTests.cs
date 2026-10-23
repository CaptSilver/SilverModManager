using CliWrap;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Backend.OS;
using SilverModManager.Backend.RuntimeDependency;
using NexusMods.Paths;
using SilverModManager.Sdk;
using NSubstitute;

namespace SilverModManager.CrossPlatform.Tests;

using Xunit;

/// <summary>
/// Registering the nxm handler from an AppImage that an AppImage manager already integrated. The data home is a
/// temp directory and the process runner is a substitute, so nothing touches the real menu or runs xdg-settings.
/// </summary>
public class RegisterUriSchemeHandlerAppImageTests : IDisposable
{
    private const string OwnDesktopFile = $"{ApplicationConstants.ApplicationId}.desktop";
    private readonly string _dataHome = Directory.CreateTempSubdirectory("register-handler").FullName;
    private readonly string _applications;
    private readonly string _appImage;
    private readonly List<string> _commands = [];
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
            return Task.FromResult<CommandResult>(null!);
        });

        var fileSystem = FileSystem.Shared.CreateOverlayFileSystem(
            new Dictionary<AbsolutePath, AbsolutePath>(),
            new Dictionary<KnownPath, AbsolutePath> { [KnownPath.XDG_DATA_HOME] = FileSystem.Shared.FromUnsanitizedFullPath(_dataHome) });

        var services = new ServiceCollection()
            .AddSingleton<Microsoft.Extensions.Logging.ILogger<LinuxInterop>>(NullLogger<LinuxInterop>.Instance)
            .AddSingleton(runner)
            .AddSingleton<IFileSystem>(fileSystem)
            .AddSingleton(new XdgSettingsDependency(runner))
            .AddSingleton(new UpdateDesktopDatabaseDependency(runner))
            .BuildServiceProvider();
        _interop = new LinuxInterop(services);
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

    private ValueTask Register(string? desktopIntegration, InstallationMethod method = InstallationMethod.AppImage)
        => _interop.RegisterUriSchemeHandler("nxm", setAsDefaultHandler: true, method, desktopIntegration, _appImage, CancellationToken.None);

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
    public async Task AnEntryWithoutTheSchemeDoesNotCount()
    {
        WriteLauncherEntry(mimeType: "x-scheme-handler/http;");

        await Register("1");

        File.Exists(Applications(OwnDesktopFile)).Should().BeTrue();
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
