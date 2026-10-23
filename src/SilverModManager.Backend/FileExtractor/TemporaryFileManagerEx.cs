using NexusMods.Abstractions.FileExtractor;
using NexusMods.Sdk.Settings;
using NexusMods.Paths;
using NexusMods.Sdk.FileExtractor;

namespace NexusMods.FileExtractor;

/// <summary>
/// Variation of <see cref="TemporaryFileManager"/> with support for injecting settings via DI.
/// </summary>
/// <remarks>
/// A manager deletes everything under its base folder when it is disposed, and every process of the app (the window,
/// a headless CLI verb) builds one on the same configured folder. So each manager works in its own
/// <c>p&lt;pid&gt;-&lt;guid&gt;</c> folder inside it, and a manager starting up removes the folders of processes that
/// are gone. Anything else in the configured folder is left alone: an older build may still be using it.
/// </remarks>
internal class TemporaryFileManagerEx : TemporaryFileManager
{
    private const string FolderPrefix = "p";

    private readonly IFileSystem _fileSystem;
    private readonly AbsolutePath _ownFolder;

    public TemporaryFileManagerEx(IFileSystem fileSystem, ISettingsManager settingsManager)
        : this(fileSystem, OwnFolderIn(fileSystem, settingsManager.Get<FileExtractorSettings>().TempFolderLocation.ToPath(fileSystem))) { }

    // The folder can vanish while the app runs, see TemporaryFolderFileSystem.
    private TemporaryFileManagerEx(IFileSystem fileSystem, AbsolutePath ownFolder)
        : this(new TemporaryFolderFileSystem(fileSystem, ownFolder), fileSystem, ownFolder) { }

    private TemporaryFileManagerEx(TemporaryFolderFileSystem wrapped, IFileSystem fileSystem, AbsolutePath ownFolder)
        : base(wrapped, basePath: wrapped.FromUnsanitizedFullPath(ownFolder.GetFullPath()))
    {
        _fileSystem = fileSystem;
        _ownFolder = ownFolder;
    }

    private static AbsolutePath OwnFolderIn(IFileSystem fileSystem, AbsolutePath sharedFolder)
    {
        RemoveFoldersOfGoneProcesses(fileSystem, sharedFolder);
        return sharedFolder / $"{FolderPrefix}{Environment.ProcessId}-{Guid.NewGuid():N}";
    }

    private static void RemoveFoldersOfGoneProcesses(IFileSystem fileSystem, AbsolutePath sharedFolder)
    {
        if (!fileSystem.DirectoryExists(sharedFolder)) return;

        foreach (var folder in fileSystem.EnumerateDirectories(sharedFolder, recursive: false))
        {
            if (OwnerPid(folder.FileName) is not { } pid || IsRunning(pid)) continue;
            try
            {
                fileSystem.DeleteDirectory(folder, recursive: true);
            }
            catch (IOException)
            {
                // Another manager starting at the same moment got there first, or a file is still held; the next
                // start tries again.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The pid in a <c>p&lt;pid&gt;-&lt;guid&gt;</c> folder name, or null for any other name.</summary>
    private static int? OwnerPid(string folderName)
    {
        if (!folderName.StartsWith(FolderPrefix, StringComparison.Ordinal)) return null;
        var dash = folderName.IndexOf('-');
        if (dash <= FolderPrefix.Length) return null;
        if (!Guid.TryParseExact(folderName[(dash + 1)..], "N", out _)) return null;
        return int.TryParse(folderName.AsSpan(FolderPrefix.Length, dash - FolderPrefix.Length), out var pid) ? pid : null;
    }

    // The app is Linux-only, so /proc is the cheapest way to ask whether a pid is alive.
    private static bool IsRunning(int pid) => Directory.Exists($"/proc/{pid}");

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        try
        {
            if (_fileSystem.DirectoryExists(_ownFolder)) _fileSystem.DeleteDirectory(_ownFolder, recursive: true);
        }
        catch (IOException)
        {
            // A file still held open; the next start removes the folder once this process has exited.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
