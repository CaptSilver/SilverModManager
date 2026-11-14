using NexusMods.Paths;

namespace SilverModManager.Library.Pickup;

/// <summary>
/// Which files in a user's folder the app will read.
/// </summary>
public static class PlainFiles
{
    private static readonly HashSet<string> InProgressExtensions = new(StringComparer.OrdinalIgnoreCase) { ".part", ".crdownload", ".download", ".tmp" };

    /// <summary>A link could point anywhere, so only real files that are not mid-download count.</summary>
    public static bool IsPlainFile(AbsolutePath file)
        => !InProgressExtensions.Contains(file.Extension.ToString()) && !IsLink(file);

    private static bool IsLink(AbsolutePath file)
    {
        var info = new FileInfo(file.ToString());
        return info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }
}
