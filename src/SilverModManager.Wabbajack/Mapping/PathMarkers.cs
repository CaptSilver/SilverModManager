using System.Text;

namespace SilverModManager.Wabbajack.Mapping;

/// <summary>What the nine placeholders a modlist leaves in MO2's files stand for.</summary>
/// <param name="StockGameFolders">
/// Names of the list's folders that hold a copy of the game ("Stock Game", "Game Root"). The app deploys their contents
/// into the real game folder and never creates them under the tools folder, so a <c>MO2_PATH</c> marker followed by one
/// of these names stands for <paramref name="GamePath"/>. See <see cref="Mo2Layout.GameRootFolders"/>.
/// </param>
/// <param name="ListFolders">
/// Names of all the list's top-level folders. Whitespace ends a path in an ini value, so <c>Game Root Backup</c> starts
/// with the stock name <c>Game Root</c> and a space; the longest folder name that fits decides, and only a stock folder
/// winning sends the path to <paramref name="GamePath"/>.
/// </param>
public readonly record struct PathMarkerValues(string GamePath, string Mo2Path, string DownloadsPath,
    IReadOnlySet<string>? StockGameFolders = null, IReadOnlySet<string>? ListFolders = null);

/// <summary>
/// Replaces the placeholders the Wabbajack compiler wrote in place of absolute paths, the way the installer does
/// (<c>{--||GAME_PATH_MAGIC_BACK||--}</c> and its DOUBLE_BACK, FORWARD, MO2_PATH and DOWNLOAD_PATH relatives).
/// </summary>
public static class PathMarkers
{
    private enum Spelling { Back, DoubleBack, Forward }

    private static readonly (string Marker, int Path, Spelling Spelling)[] Markers = BuildMarkers();

    private static (string, int, Spelling)[] BuildMarkers()
    {
        var markers = new List<(string, int, Spelling)>();
        string[] families = ["GAME_PATH", "MO2_PATH", "DOWNLOAD_PATH"];
        for (var path = 0; path < families.Length; path++)
        {
            markers.Add(($"{{--||{families[path]}_MAGIC_BACK||--}}", path, Spelling.Back));
            markers.Add(($"{{--||{families[path]}_MAGIC_DOUBLE_BACK||--}}", path, Spelling.DoubleBack));
            markers.Add(($"{{--||{families[path]}_MAGIC_FORWARD||--}}", path, Spelling.Forward));
        }
        return markers.ToArray();
    }

    /// <summary>
    /// With <paramref name="nativeSpelling"/> false the values are Windows paths and each marker gets the spelling
    /// its name asks for. With it true the values are host paths, which are used as they are, and the backslashes
    /// that follow a marker in the same whitespace- or quote-delimited token (<c>{GAME}\data</c>) become
    /// <c>/</c>, since the tools read the result on a Unix path.
    /// </summary>
    public static string Substitute(string text, PathMarkerValues values, bool nativeSpelling)
    {
        var result = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] != '{' || !TryMatch(text, i, out var marker))
            {
                result.Append(text[i++]);
                continue;
            }

            i += marker.Marker.Length;
            var path = marker.Path;
            if (path == 1 && StockFolderLength(text, i, values) is var stockLength and > 0)
            {
                path = 0;
                i += stockLength;
            }

            var value = path switch
            {
                0 => values.GamePath,
                1 => values.Mo2Path,
                _ => values.DownloadsPath,
            };

            if (nativeSpelling)
            {
                result.Append(value);
                // Only the run right after the marker is a path continuation; another marker ends it.
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '"' && text[i] != '\'' && text[i] != '{')
                {
                    result.Append(text[i] == '\\' ? '/' : text[i]);
                    i++;
                }
                continue;
            }

            result.Append(marker.Spelling switch
            {
                Spelling.Back => value,
                Spelling.DoubleBack => value.Replace("\\", "\\\\"),
                _ => value.Replace('\\', '/'),
            });
        }
        return result.ToString();
    }

    /// <summary>
    /// The length of a separator (<c>\</c>, <c>\\</c> or <c>/</c>) and stock-game folder name at <paramref name="index"/>,
    /// or 0 when the text there is some other path. The longest folder name of the list wins, and it must end the path segment.
    /// </summary>
    private static int StockFolderLength(string text, int index, PathMarkerValues values)
    {
        var stock = values.StockGameFolders;
        if (stock is null || stock.Count == 0) return 0;

        var separator = text.AsSpan(index).StartsWith("\\\\") ? 2 : index < text.Length && text[index] is '\\' or '/' ? 1 : 0;
        if (separator == 0) return 0;

        var start = index + separator;
        var best = 0;
        var bestIsStock = false;
        foreach (var folder in stock.Concat(values.ListFolders ?? Enumerable.Empty<string>()))
        {
            if (folder.Length <= best || !EqualsIgnoreCase(text, start, folder)) continue;

            var end = start + folder.Length;
            if (end < text.Length && text[end] is not ('\\' or '/' or '"' or '\'' or '{') && !char.IsWhiteSpace(text[end])) continue;
            best = folder.Length;
            bestIsStock = stock.Contains(folder);
        }
        return bestIsStock ? separator + best : 0;
    }

    private static bool EqualsIgnoreCase(string text, int start, string folder)
        => start + folder.Length <= text.Length && string.Compare(text, start, folder, 0, folder.Length, StringComparison.OrdinalIgnoreCase) == 0;

    private static bool TryMatch(string text, int index, out (string Marker, int Path, Spelling Spelling) found)
    {
        foreach (var candidate in Markers)
        {
            if (string.CompareOrdinal(text, index, candidate.Marker, 0, candidate.Marker.Length) != 0) continue;
            found = candidate;
            return true;
        }
        found = default;
        return false;
    }
}
