using System.Text;
using NexusMods.Paths;

namespace NexusMods.Games.FromSoft.Me3;

/// <summary>
/// The text of the me3 profile, as a pure function of the files the loadout puts in the mods folder. Every apply
/// writes the whole file; nothing from a previous one is carried over, so any order of installs and removals that
/// reaches the same set of files gives the same text.
/// </summary>
public static class Me3Profile
{
    /// <summary>
    /// The TOML table that lists the content packages. me3's own shipped profile uses the singular form, and the
    /// generated file has not been checked against me3 0.13: nothing has run it. One constant, so the check is a
    /// one-line change.
    /// </summary>
    public const string PackageTableKey = "packages";

    /// <summary>
    /// The top-level key that points a modded session at <see cref="Me3ProfileSpec.ModdedSave"/>. Neither the key nor
    /// its placement after <c>profileVersion</c> has been checked against me3 0.13; nothing else writes it. The key is
    /// spelled as me3's profile v1 reads it.
    /// </summary>
    public const string SaveFileKey = "savefile";

    /// <summary>
    /// Renders the profile.
    /// </summary>
    /// <param name="spec">The game's name for me3, its modded save and the natives its overlay can hold.</param>
    /// <param name="relativePaths">The paths, relative to the mods folder, of the files the loadout deploys there.</param>
    /// <param name="modsFolder">The mods folder; package and native paths are absolute inside it.</param>
    /// <param name="gameFolder">
    /// The game's folder. An overlay native is named inside it, a path that exists only inside the session's overlay.
    /// </param>
    /// <exception cref="InvalidOperationException">A native mod's folder has no single DLL to load.</exception>
    public static string Render(Me3ProfileSpec spec, IEnumerable<string> relativePaths, AbsolutePath modsFolder, AbsolutePath gameFolder)
    {
        var packages = new SortedSet<string>(StringComparer.Ordinal);
        var nativeFiles = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var overlayNatives = new HashSet<Me3OverlayNative>();

        foreach (var path in relativePaths)
        {
            var parts = path.Split('/', 2);
            if (parts.Length < 2) continue;

            var (top, rest) = (parts[0], parts[1]);
            if (top.Equals(Me3ModsLayout.OverlayFolder, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var native in spec.OverlayNatives)
                {
                    if (path.Equals(native.TriggerPathInMods, StringComparison.OrdinalIgnoreCase)) overlayNatives.Add(native);
                }
            }
            else if (top.Equals(Me3ModsLayout.NativesFolder, StringComparison.OrdinalIgnoreCase))
            {
                var native = rest.Split('/', 2);
                if (native.Length < 2) continue;
                if (!nativeFiles.TryGetValue(native[0], out var files)) nativeFiles.Add(native[0], files = []);
                files.Add(native[1]);
            }
            else
            {
                packages.Add(top);
            }
        }

        var nativePaths = new List<string>();
        foreach (var native in spec.OverlayNatives.Where(overlayNatives.Contains))
            nativePaths.Add((gameFolder / native.GameRelativeDllPath).ToString());
        foreach (var (id, files) in nativeFiles)
        {
            var dll = Me3NativeDll.Find(files, id)
                ?? throw new InvalidOperationException(DescribeUnloadableNative(id, files));
            nativePaths.Add((modsFolder / Me3ModsLayout.NativesFolder / id / dll).ToString());
        }

        var modded = packages.Count > 0 || nativePaths.Count > 0;
        var text = new StringBuilder();
        text.Append("profileVersion = \"v1\"\n");
        if (modded && spec.ModdedSave is not null && !overlayNatives.Any(native => native.OwnsSave))
            text.Append($"{SaveFileKey} = \"{spec.ModdedSave}\"\n");
        text.Append($"\n[[supports]]\ngame = \"{spec.GameName}\"\n");

        foreach (var nativePath in nativePaths)
            text.Append($"\n[[natives]]\npath = {TomlString(nativePath)}\n");

        foreach (var id in packages)
            text.Append($"\n[[{PackageTableKey}]]\nid = {BasicString(id)}\npath = {TomlString($"{modsFolder}/{id}/")}\n");

        return text.ToString();
    }

    private static string DescribeUnloadableNative(string id, List<string> files)
    {
        var dlls = Me3NativeDll.ListDlls(files);
        return dlls.Length == 0
            ? $"The native mod '{id}' has no DLL for me3 to load."
            : $"The native mod '{id}' has several DLLs and none is named like the mod, so me3 cannot tell which to load: {string.Join(", ", dlls)}.";
    }

    /// <summary>A single-quoted literal, which needs no escaping; a path holding a quote falls back to a basic string.</summary>
    private static string TomlString(string value) => value.Contains('\'') ? BasicString(value) : $"'{value}'";

    private static string BasicString(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
}
