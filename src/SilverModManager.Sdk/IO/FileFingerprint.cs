using NexusMods.Paths;

namespace SilverModManager.Sdk.IO;

/// <summary>
/// The size and modification time the index records for a file, taken from what a symlink leads to.
/// </summary>
/// <remarks>
/// <c>FileInfo</c> describes a symlink itself: its length is the length of the target path and its time is
/// when the link was made. A file deployed as a symlink into the staging store would then look unchanged
/// after the game wrote through it, and look changed on every scan because the link's length is never the
/// size that was recorded for the content.
/// </remarks>
public readonly record struct FileFingerprint(Size Size, DateTimeOffset LastModified)
{
    /// <summary>
    /// The fingerprint of the file at <paramref name="path"/>, or of its final target when it is a symlink.
    /// Throws <see cref="FileNotFoundException"/> for a symlink that leads nowhere.
    /// </summary>
    public static FileFingerprint Of(AbsolutePath path)
    {
        var info = new FileInfo(path.GetFullPath());
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            info = new FileInfo(target.FullName);

        // Length throws FileNotFoundException when there is nothing at the path.
        return new FileFingerprint(Size.From((ulong)info.Length), new DateTimeOffset(info.LastWriteTimeUtc));
    }
}
