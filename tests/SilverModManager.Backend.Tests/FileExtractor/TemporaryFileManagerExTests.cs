using FluentAssertions;
using SilverModManager.FileExtractor;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.FileExtractor;
using SilverModManager.Sdk.Settings;
using NSubstitute;

namespace SilverModManager.Backend.Tests.FileExtractor;

public sealed class TemporaryFileManagerExTests : IDisposable
{
    private readonly IFileSystem _fileSystem = FileSystem.Shared;
    private readonly AbsolutePath _root;
    private readonly AbsolutePath _tempFolder;
    private readonly ISettingsManager _settings;
    private readonly TemporaryFileManagerEx _manager;

    public TemporaryFileManagerExTests()
    {
        var name = "smm-tempmanager-tests-" + Guid.NewGuid().ToString("N");
        _root = _fileSystem.GetKnownPath(KnownPath.TempDirectory) / name;
        _tempFolder = _root / "Temp";

        _settings = Substitute.For<ISettingsManager>();
        _settings.Get<FileExtractorSettings>().Returns(new FileExtractorSettings
        {
            TempFolderLocation = new ConfigurablePath(KnownPath.TempDirectory, $"{name}/Temp"),
        });
        _manager = new TemporaryFileManagerEx(_fileSystem, _settings);
    }

    public void Dispose()
    {
        _manager.Dispose();
        if (_fileSystem.DirectoryExists(_root)) _fileSystem.DeleteDirectory(_root, recursive: true);
    }

    [Fact]
    public async Task AFileCanBeWrittenAfterTheTempFolderWasDeleted()
    {
        _fileSystem.DeleteDirectory(_tempFolder, recursive: true);

        using var file = _manager.CreateFile();
        await file.Path.WriteAllTextAsync("hello");

        (await file.Path.ReadAllTextAsync()).Should().Be("hello");
        file.Path.Parent.Parent.Should().Be(_tempFolder);
    }

    [Fact]
    public void AFileCanBeOpenedForWritingAfterTheTempFolderWasDeleted()
    {
        _fileSystem.DeleteDirectory(_tempFolder, recursive: true);

        using var file = _manager.CreateFile();
        using (var stream = file.Path.Open(FileMode.Create, FileAccess.Write))
        {
            stream.WriteByte(1);
        }

        file.Path.FileExists.Should().BeTrue();
    }

    [Fact]
    public void AFolderCanBeCreatedAfterTheTempFolderWasDeleted()
    {
        _fileSystem.DeleteDirectory(_tempFolder, recursive: true);

        using var folder = _manager.CreateFolder();

        folder.Path.DirectoryExists().Should().BeTrue();
    }

    [Fact]
    public async Task ADeletedTempFileDoesNotFailItsDisposal()
    {
        var file = _manager.CreateFile();
        await file.Path.WriteAllTextAsync("x");
        _fileSystem.DeleteDirectory(_tempFolder, recursive: true);

        var dispose = () => file.Dispose();

        dispose.Should().NotThrow();
    }

    [Fact]
    public async Task DisposingOneManagerLeavesAnotherManagersFilesOnTheSameTempFolder()
    {
        // Every process of the app (the window, a headless CLI verb) builds a manager on the same configured folder,
        // and a manager deletes its files when it is disposed.
        var other = new TemporaryFileManagerEx(_fileSystem, _settings);
        var file = _manager.CreateFile();
        await file.Path.WriteAllTextAsync("in flight");

        other.Dispose();

        file.Path.FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task AFolderLeftByAProcessThatIsGoneIsRemovedWhenAManagerStarts()
    {
        // Larger than Linux's pid_max, so no live process has it.
        var stale = _tempFolder / "p999999999-0123456789abcdef0123456789abcdef";
        _fileSystem.CreateDirectory(stale);
        await (stale / "left.tmp").WriteAllTextAsync("x");

        using var next = new TemporaryFileManagerEx(_fileSystem, _settings);

        _fileSystem.DirectoryExists(stale).Should().BeFalse();
    }

    [Fact]
    public async Task AFolderOfARunningProcessIsKeptWhenAManagerStarts()
    {
        var live = _tempFolder / $"p{Environment.ProcessId}-0123456789abcdef0123456789abcdef";
        _fileSystem.CreateDirectory(live);
        await (live / "busy.tmp").WriteAllTextAsync("x");

        using var next = new TemporaryFileManagerEx(_fileSystem, _settings);

        (live / "busy.tmp").FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task AFileOutsideTheManagersFoldersIsLeftAlone()
    {
        // Files an older build put straight into the folder may still belong to a running copy of it.
        var loose = _tempFolder / "older-build.tmp";
        await loose.WriteAllTextAsync("x");

        using var next = new TemporaryFileManagerEx(_fileSystem, _settings);
        next.Dispose();

        loose.FileExists.Should().BeTrue();
    }

    [Fact]
    public void DisposingAManagerRemovesItsOwnFolder()
    {
        var next = new TemporaryFileManagerEx(_fileSystem, _settings);
        var folder = next.CreateFile().Path.Parent;
        _fileSystem.CreateDirectory(folder);

        next.Dispose();

        _fileSystem.DirectoryExists(folder).Should().BeFalse();
    }
}
