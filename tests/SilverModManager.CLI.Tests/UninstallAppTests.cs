using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.Commandline;
using SilverModManager.Abstractions.Games;
using SilverModManager.CrossPlatform;
using SilverModManager.DataModel;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.Sdk.FileExtractor;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>uninstall-app</c> run in-process against data and state folders in a temp directory; the real ones are never
/// reached because the file system maps both known folders there.
/// </summary>
public sealed class UninstallAppTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("uninstall-app-tests").FullName;
    private readonly IFileSystem _fileSystem;
    private readonly ISettingsManager _settings = Substitute.For<ISettingsManager>();
    private readonly LoggingRenderer _renderer = new();

    public UninstallAppTests()
    {
        var shared = NexusMods.Paths.FileSystem.Shared;
        _fileSystem = shared.CreateOverlayFileSystem(
            new Dictionary<AbsolutePath, AbsolutePath>(),
            new Dictionary<KnownPath, AbsolutePath>
            {
                { KnownPath.XDG_DATA_HOME, shared.FromUnsanitizedFullPath(Path.Combine(_root, "data")) },
                { KnownPath.XDG_STATE_HOME, shared.FromUnsanitizedFullPath(Path.Combine(_root, "state")) },
            });

        _settings.Get<DataModelSettings>().Returns(DataModelSettings.CreateDefault(null!));
        _settings.Get<FileExtractorSettings>().Returns(FileExtractorSettings.CreateDefault(null!));
        _settings.Get<LoggingSettings>().Returns(LoggingSettings.CreateDefault());
    }

    public void Dispose()
    {
        foreach (var path in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.Delete(_root, recursive: true);
    }

    private AbsolutePath DataFolder => DataModelSettings.GetLocalApplicationDataDirectory(_fileSystem);
    private AbsolutePath LogFolder => LoggingSettings.GetLogBaseFolder(_fileSystem);

    private string Text => string.Concat(_renderer.Logs.OfType<Text>().Select(t => string.Format(t.Template, t.Arguments ?? [])));

    private Task<int> Uninstall() => CleanupVerbs.Uninstall(_renderer, NullLogger.Instance, _settings, _fileSystem, Substitute.For<ISynchronizerService>(), []);

    private void WriteAppFiles()
    {
        (DataFolder / "DataModel/MnemonicDB.rocksdb").CreateDirectory();
        File.WriteAllText((DataFolder / "DataModel/MnemonicDB.rocksdb/CURRENT").ToString(), "db");
        LogFolder.CreateDirectory();
        File.WriteAllText((LogFolder / "SilverModManager.main.current.log").ToString(), "log");
    }

    [Fact]
    public async Task ASuccessfulUninstall_ReturnsZero_AndRemovesTheDataAndTheLogs()
    {
        WriteAppFiles();

        var exitCode = await Uninstall();

        exitCode.Should().Be(0);
        DataFolder.DirectoryExists().Should().BeFalse();
        LogFolder.DirectoryExists().Should().BeFalse();
        Text.Should().Contain("Application uninstalled successfully");
    }

    [Fact]
    public async Task AFolderThatCannotBeDeleted_FailsWithASentence_AndKeepsTheLogsTheSentencePointsTo()
    {
        WriteAppFiles();
        // The data folder cannot be removed from a parent that is read-only.
        File.SetUnixFileMode(DataFolder.Parent.ToString(), UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var exitCode = await Uninstall();

        exitCode.Should().Be(-1);
        Text.Should().Contain("Failed to delete directory").And.Contain("Details are in the log at")
            .And.NotContain("   at ").And.NotContain("UnauthorizedAccessException")
            .And.NotContain("Application uninstalled successfully");
        LogFolder.DirectoryExists().Should().BeTrue();
        (LogFolder / "SilverModManager.main.current.log").FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task SettingsThatCannotBeRead_FailWithASentence_AndKeepTheLogs()
    {
        WriteAppFiles();
        _settings.Get<LoggingSettings>().Returns(_ => throw new InvalidOperationException("bad settings"));

        var exitCode = await Uninstall();

        exitCode.Should().Be(-1);
        Text.Should().Contain("Details are in the log at").And.NotContain("bad settings");
        LogFolder.DirectoryExists().Should().BeTrue();
    }
}
