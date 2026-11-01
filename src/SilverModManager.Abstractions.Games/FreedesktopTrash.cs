using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace SilverModManager.Abstractions.Games;

/// <summary>
/// The home trash of the freedesktop.org Trash specification: <c>Trash/files/&lt;name&gt;</c> holds the folder and
/// <c>Trash/info/&lt;name&gt;.trashinfo</c> records where it came from, which is what lets a file manager restore it.
/// </summary>
/// <remarks>
/// Only the home trash is used. A folder on another file system than <c>$XDG_DATA_HOME</c> is refused rather than
/// copied: the spec keeps one trash per file system for this reason, and a copy-then-delete would be a delete.
/// </remarks>
public sealed class FreedesktopTrash : ITrash
{
    private const int CrossDeviceError = 18; // EXDEV

    private readonly string _trashRoot;
    private readonly TimeProvider _time;
    private readonly Action<string, string> _move;

    /// <summary>
    /// A trash under <paramref name="dataHome"/> (<c>$XDG_DATA_HOME</c>) that moves with <c>rename(2)</c>.
    /// </summary>
    public FreedesktopTrash(string dataHome, TimeProvider? time = null) : this(dataHome, time ?? TimeProvider.System, Rename) { }

    /// <summary>
    /// As the other constructor, with <paramref name="move"/> in place of the system call so a test can make the move fail.
    /// </summary>
    public FreedesktopTrash(string dataHome, TimeProvider time, Action<string, string> move)
    {
        _trashRoot = Path.Combine(dataHome, "Trash");
        _time = time;
        _move = move;
    }

    /// <inheritdoc />
    public void MoveToTrash(string path)
    {
        var source = path.TrimEnd('/');
        if (!Directory.Exists(source)) throw new HostFolderMoveRefusedException($"'{source}' is no longer there, so nothing was moved to the trash.");

        var files = Path.Combine(_trashRoot, "files");
        var info = Path.Combine(_trashRoot, "info");
        Directory.CreateDirectory(files);
        Directory.CreateDirectory(info);

        var (name, infoPath) = ClaimName(Path.GetFileName(source), files, info, source);
        try
        {
            _move(source, Path.Combine(files, name));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            File.Delete(infoPath);
            throw new HostFolderMoveRefusedException(
                $"'{source}' could not be moved to the trash ({e.Message}). It is on a different drive than your trash or cannot be moved, so it was left where it is.", e);
        }
    }

    /// <summary>
    /// Writes the info file under the first free name. Creating it exclusively is the spec's way of reserving the name.
    /// </summary>
    private (string Name, string InfoPath) ClaimName(string baseName, string files, string info, string source)
    {
        var contents = "[Trash Info]\n" +
                       $"Path={EncodePath(source)}\n" +
                       $"DeletionDate={_time.GetLocalNow().ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)}\n";

        for (var attempt = 1;; attempt++)
        {
            var name = attempt == 1 ? baseName : $"{baseName}.{attempt}";
            var infoPath = Path.Combine(info, name + ".trashinfo");
            if (Path.Exists(Path.Combine(files, name))) continue;

            try
            {
                using var stream = new FileStream(infoPath, FileMode.CreateNew, FileAccess.Write);
                stream.Write(Encoding.UTF8.GetBytes(contents));
                return (name, infoPath);
            }
            catch (IOException) when (File.Exists(infoPath))
            {
                // Another entry already holds this name.
            }
        }
    }

    private static string EncodePath(string path)
        => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static void Rename(string from, string to)
    {
        if (rename(from, to) == 0) return;

        var errno = Marshal.GetLastPInvokeError();
        throw new IOException(errno == CrossDeviceError ? "the folders are on different file systems" : new System.ComponentModel.Win32Exception(errno).Message);
    }

    [DllImport("libc", EntryPoint = "rename", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int rename([MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);
}
