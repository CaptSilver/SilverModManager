using System.Reflection;
using NexusMods.Paths;

namespace SilverModManager.Platform.Proton.Steam.Shortcuts;

/// <summary>
/// Writes the images Steam shows for the shortcut into an account's <c>grid</c> folder, named after the app id
/// Steam stored.
/// </summary>
public static class SteamGridArt
{
    private const string IconFileName = "silvermodmanager.png";

    /// <summary>
    /// Writes every image, each to a temporary name and then renamed, so Steam never reads half a file. Returns the
    /// files written.
    /// </summary>
    /// <exception cref="IOException">A file could not be written. The ones before it stay.</exception>
    public static IReadOnlyList<AbsolutePath> Write(AbsolutePath gridDirectory, uint appId)
    {
        (string Resource, string Name)[] files =
        [
            ("portrait", $"{appId}p.png"),
            ("wide", $"{appId}.png"),
            ("hero", $"{appId}_hero.png"),
            ("logo", $"{appId}_logo.png"),
            ("icon", $"{appId}_icon.png"),
            // Big Picture and Game Mode look the grid up by the 64-bit id.
            ("portrait", $"{SteamShortcutId.LaunchId(appId)}.png"),
        ];

        try { gridDirectory.CreateDirectory(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException) { throw new IOException(ex.Message, ex); }
        return files.Select(f => Extract(f.Resource, gridDirectory.Combine(f.Name))).ToList();
    }

    /// <summary>
    /// Extracts the icon that <see cref="SteamShortcutEntry.Icon"/> points at.
    /// </summary>
    public static AbsolutePath WriteIcon(AbsolutePath iconsDirectory)
    {
        iconsDirectory.CreateDirectory();
        return Extract("icon", iconsDirectory.Combine(IconFileName));
    }

    private static AbsolutePath Extract(string resource, AbsolutePath target)
    {
        var name = $"Steam.Art.silvermodmanager-{resource}.png";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"The embedded image {name} is missing from the build.");

        var temp = target.ToString() + ".tmp";
        try
        {
            using (var output = File.Create(temp)) stream.CopyTo(output);
            File.Move(temp, target.ToString(), overwrite: true);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException($"{target} could not be written: {ex.Message}", ex);
        }
        return target;
    }
}
