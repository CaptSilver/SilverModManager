using System.Text;

namespace SilverModManager.Wabbajack.Mapping;

/// <summary>What the nine placeholders a modlist leaves in MO2's files stand for.</summary>
public readonly record struct PathMarkerValues(string GamePath, string Mo2Path, string DownloadsPath);

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

            var value = marker.Path switch
            {
                0 => values.GamePath,
                1 => values.Mo2Path,
                _ => values.DownloadsPath,
            };
            i += marker.Marker.Length;

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
