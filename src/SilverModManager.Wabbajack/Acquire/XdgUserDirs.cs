using NexusMods.Paths;

namespace SilverModManager.Wabbajack.Acquire;

/// <summary>Finds the user's download folder the way <c>xdg-user-dir DOWNLOAD</c> does, without running it.</summary>
public static class XdgUserDirs
{
    private const string Key = "XDG_DOWNLOAD_DIR=";
    private const string HomePrefix = "$HOME/";

    /// <summary>
    /// The folder <c>XDG_DOWNLOAD_DIR</c> names in <c>configHome/user-dirs.dirs</c>, or <c>home/Downloads</c> when the
    /// file, the line or a usable value is missing. Only a quoted <c>"$HOME/..."</c> or quoted absolute path counts;
    /// anything the shell would have to interpret differently is not guessed at.
    /// </summary>
    public static AbsolutePath Download(AbsolutePath home, AbsolutePath configHome)
    {
        var fallback = home.Combine("Downloads");
        var file = configHome.Combine("user-dirs.dirs");
        if (!file.FileExists) return fallback;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(file.ToString());
        }
        catch (IOException)
        {
            return fallback;
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (!line.StartsWith(Key, StringComparison.Ordinal)) continue;
            return Resolve(line[Key.Length..], home) ?? fallback;
        }
        return fallback;
    }

    /// <summary>The folder for the real home and config folder of <paramref name="fileSystem"/>.</summary>
    public static AbsolutePath Download(IFileSystem fileSystem)
        => Download(fileSystem.GetKnownPath(KnownPath.HomeDirectory), fileSystem.GetKnownPath(KnownPath.XDG_CONFIG_HOME));

    private static AbsolutePath? Resolve(string value, AbsolutePath home)
    {
        if (value.Length < 3 || value[0] != '"' || value[^1] != '"') return null;
        var inner = value[1..^1];

        try
        {
            if (inner.StartsWith(HomePrefix, StringComparison.Ordinal) && inner.Length > HomePrefix.Length)
                return home.Combine(RelativePath.FromUnsanitizedInput(inner[HomePrefix.Length..]));
            if (inner.StartsWith('/') && inner.Length > 1)
                return home.FileSystem.FromUnsanitizedFullPath(inner);
        }
        catch (ArgumentException)
        {
            // A value the path library rejects is no better than a missing one.
        }
        return null;
    }
}
