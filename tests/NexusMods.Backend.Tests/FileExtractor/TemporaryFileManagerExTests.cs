using FluentAssertions;
using NexusMods.FileExtractor;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.FileExtractor;
using NexusMods.Sdk.Settings;
using NSubstitute;

namespace NexusMods.Backend.Tests.FileExtractor;

public sealed class TemporaryFileManagerExTests : IDisposable
{
    private readonly IFileSystem _fileSystem = FileSystem.Shared;
    private readonly AbsolutePath _root;
    private readonly AbsolutePath _tempFolder;
    private readonly TemporaryFileManagerEx _manager;

    public TemporaryFileManagerExTests()
    {
        var name = "smm-tempmanager-tests-" + Guid.NewGuid().ToString("N");
        _root = _fileSystem.GetKnownPath(KnownPath.TempDirectory) / name;
        _tempFolder = _root / "Temp";

        var settings = Substitute.For<ISettingsManager>();
        settings.Get<FileExtractorSettings>().Returns(new FileExtractorSettings
        {
            TempFolderLocation = new ConfigurablePath(KnownPath.TempDirectory, $"{name}/Temp"),
        });
        _manager = new TemporaryFileManagerEx(_fileSystem, settings);
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
        file.Path.Parent.Should().Be(_tempFolder);
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
}
