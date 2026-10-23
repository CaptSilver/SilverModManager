using NexusMods.Abstractions.FileExtractor;
using NexusMods.Sdk.Settings;
using NexusMods.Paths;
using NexusMods.Sdk.FileExtractor;

namespace NexusMods.FileExtractor;

/// <summary>
/// Variation of <see cref="TemporaryFileManager"/> with support for injecting settings via DI.
/// </summary>
internal class TemporaryFileManagerEx : TemporaryFileManager
{
    public TemporaryFileManagerEx(IFileSystem fileSystem, ISettingsManager settingsManager)
        : this(fileSystem, settingsManager.Get<FileExtractorSettings>().TempFolderLocation.ToPath(fileSystem)) { }

    // The folder can vanish while the app runs, see TemporaryFolderFileSystem.
    private TemporaryFileManagerEx(IFileSystem fileSystem, AbsolutePath basePath)
        : this(new TemporaryFolderFileSystem(fileSystem, basePath), basePath) { }

    private TemporaryFileManagerEx(TemporaryFolderFileSystem fileSystem, AbsolutePath basePath)
        : base(fileSystem, basePath: fileSystem.FromUnsanitizedFullPath(basePath.GetFullPath())) { }
}
