using NexusMods.Paths;

namespace NexusMods.Wabbajack.Mapping;

/// <summary>
/// Gives every path of a modlist one spelling. Lists are built on Windows, where <c>Mods\A</c> and <c>mods\a</c> are
/// the same folder, and a case-sensitive filesystem would otherwise end up with both.
/// </summary>
public static class PathCasing
{
    /// <summary>
    /// Maps each path to its unified spelling: each folder level takes the spelling it was first seen with, in the
    /// order of <paramref name="paths"/>.
    /// </summary>
    public static IReadOnlyDictionary<RelativePath, RelativePath> Unify(IEnumerable<RelativePath> paths)
    {
        var spellings = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new Dictionary<RelativePath, RelativePath>();
        foreach (var path in paths)
        {
            if (result.ContainsKey(path)) continue;

            var unified = new List<string>();
            var lowered = "";
            foreach (var part in path.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                lowered = lowered.Length == 0 ? part.ToLowerInvariant() : lowered + "/" + part.ToLowerInvariant();
                if (!spellings.TryGetValue(lowered, out var spelling))
                {
                    spelling = part;
                    spellings[lowered] = spelling;
                }
                unified.Add(spelling);
            }
            result[path] = RelativePath.FromUnsanitizedInput(string.Join('/', unified));
        }
        return result;
    }
}
