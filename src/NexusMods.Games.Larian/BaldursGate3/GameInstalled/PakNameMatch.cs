using System.Text.RegularExpressions;

namespace NexusMods.Games.Larian.BaldursGate3.GameInstalled;

/// <summary>
/// The naming the game gives a pak it downloads from mod.io: the module folder (cut short when it is long),
/// a '-', and four characters from [a-z0-9]. Stems are compared as written; the game writes them lowercase.
/// </summary>
public static partial class PakNameMatch
{
    private const int TailLength = 4;

    [GeneratedRegex("^[a-z0-9_.\\-]{1,30}-[a-z0-9]{4}$")]
    private static partial Regex InGameStem();

    /// <summary>
    /// Splits <paramref name="stem"/> at its last '-' into a non-empty head and a four character
    /// [a-z0-9] tail. The head can itself hold '-'.
    /// </summary>
    public static bool TrySplit(string stem, out string head)
    {
        head = "";
        var dash = stem.LastIndexOf('-');
        if (dash <= 0 || stem.Length - dash - 1 != TailLength) return false;

        foreach (var c in stem.AsSpan(dash + 1))
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9'))) return false;
        }

        head = stem[..dash];
        return true;
    }

    /// <summary>
    /// True when the head of <paramref name="stem"/> is the lowercased <paramref name="folder"/> or a prefix of
    /// it, the game having cut the folder short.
    /// </summary>
    public static bool MatchesFolder(string stem, string folder)
        => TrySplit(stem, out var head) && folder.ToLowerInvariant().StartsWith(head, StringComparison.Ordinal);

    /// <summary>True when the raw stem has the shape of a pak the game downloaded.</summary>
    public static bool LooksInGame(string stem) => InGameStem().IsMatch(stem);
}
