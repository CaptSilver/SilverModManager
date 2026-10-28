using NexusMods.Paths;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Backend.Games;

/// <summary>
/// The game folder could not be indexed because the disk failed: a file could not be read, the folder could not be
/// listed, or the indexed batch could not be saved. That ends the index: the drive is failing or went away, and
/// carrying on would only report more of the same. The batches already indexed stay.
/// </summary>
public sealed class GameFolderUnreadableException : UserFacingException
{
    private readonly GameInstallation _installation;

    /// <summary>A file of the folder could not be read.</summary>
    public GameFolderUnreadableException(GameInstallation installation, AbsolutePath file, IOException cause)
        : this(installation, $"Reading {file}", cause) { }

    /// <summary><paramref name="whatFailed"/> names the step in a sentence start, such as "Listing the files in X".</summary>
    public GameFolderUnreadableException(GameInstallation installation, string whatFailed, IOException cause)
        : base($"{whatFailed} failed: {cause.Message}. Check the drive and try again; nothing was changed.", cause)
    {
        _installation = installation;
    }

    /// <inheritdoc />
    public override string Title => $"{_installation.Game.DisplayName}'s folder could not be read";

    /// <summary>
    /// Whether the error means the device cannot deliver bytes (EIO and its relatives, or their Windows
    /// counterparts), as opposed to a problem with one file such as a socket, a symlink loop or a permission.
    /// The runtime reports the raw error number as the exception's HResult on Unix and wraps the Win32 code
    /// in an HRESULT on Windows.
    /// </summary>
    public static bool IsDeviceFailure(IOException error)
    {
        var code = error.HResult;
        if (!OperatingSystem.IsWindows()) return code is 5 or 107 or 116 or 117 or 121 or 123; // EIO, ENOTCONN, ESTALE, EUCLEAN, EREMOTEIO, ENOMEDIUM

        if ((uint)code >> 16 != 0x8007) return false;
        return (code & 0xFFFF) is 23 or 1117 or 1167 or 1393; // CRC, IO device, device not connected, corrupt disk
    }
}
