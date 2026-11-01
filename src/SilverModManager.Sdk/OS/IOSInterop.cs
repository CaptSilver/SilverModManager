using JetBrains.Annotations;
using NexusMods.Paths;

namespace SilverModManager.Sdk;

/// <summary>
/// OS-specific functionality.
/// </summary>
[PublicAPI]
public interface IOSInterop
{
    /// <summary>
    /// Gets the path to executable of the current process.
    /// </summary>
    AbsolutePath GetRunningExecutablePath(out string rawPath);

    /// <summary>
    /// Opens the Uri in the default application handler based on the Uri scheme.
    /// </summary>
    /// <seealso cref="OpenUri"/>
    void OpenUri(Uri uri);

    /// <summary>
    /// Raised when <see cref="OpenUri"/> could not hand the Uri to any handler, so the user can be given the link
    /// to open by hand. May be raised from any thread.
    /// </summary>
    event Action<Uri>? OpenUriFailed;

    /// <summary>
    /// Opens the file in the default application handler.
    /// </summary>
    void OpenFile(AbsolutePath filePath);

    /// <summary>
    /// Opens the directory with the system explorer.
    /// </summary>
    void OpenDirectory(AbsolutePath directoryPath);

    /// <summary>
    /// Opens the directory with the system explorer and highlights the file.
    /// </summary>
    void OpenFileInDirectory(AbsolutePath filePath);

    /// <summary>
    /// Gets all file system mounts.
    /// </summary>
    ValueTask<FileSystemMount[]> GetFileSystemMounts(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the file system mount that hosts the file or directory at the given path.
    /// </summary>
    ValueTask<FileSystemMount?> GetFileSystemMount(AbsolutePath path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes this app the handler for <c>scheme://</c> links.
    /// </summary>
    /// <param name="scheme">The scheme without the colon, e.g. <c>nxm</c>.</param>
    /// <param name="claim">Whether to take the scheme from another app that holds it.</param>
    /// <param name="setAsDefaultHandler">Whether to also make this app the default handler, not just a candidate.</param>
    ValueTask<UriSchemeRegistration> RegisterUriSchemeHandler(string scheme, UriSchemeClaim claim = UriSchemeClaim.Take, bool setAsDefaultHandler = true, CancellationToken cancellationToken = default);
}

/// <summary>
/// How <see cref="IOSInterop.RegisterUriSchemeHandler"/> treats a scheme another app already holds.
/// </summary>
public enum UriSchemeClaim
{
    /// <summary>Register this app as the handler, whoever has the scheme.</summary>
    Take,

    /// <summary>Register this app only when the scheme is unset or already ours.</summary>
    IfUnclaimed,
}

/// <summary>
/// What a call to <see cref="IOSInterop.RegisterUriSchemeHandler"/> found and did.
/// </summary>
/// <param name="Registered">False only for <see cref="UriSchemeClaim.IfUnclaimed"/> when <paramref name="OtherHandler"/> holds the scheme.</param>
/// <param name="OtherHandler">The desktop file that held the default before the call, null when it was unset or ours.</param>
public readonly record struct UriSchemeRegistration(bool Registered, string? OtherHandler);

/// <summary>
/// Represents a file system mount.
/// </summary>
[PublicAPI]
public record FileSystemMount(string Source, AbsolutePath Target, string Type, Size BytesTotal, Size BytesAvailable)
{
    public Size BytesUsed => BytesTotal - BytesAvailable;
}
