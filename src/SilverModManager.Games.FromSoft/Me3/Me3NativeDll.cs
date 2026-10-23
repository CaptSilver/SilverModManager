namespace SilverModManager.Games.FromSoft.Me3;

/// <summary>
/// Picks the DLL me3 loads out of a native mod's files. Archives that carry a redistributable beside the real
/// DLL would otherwise load the dependency and leave the mod dormant, so a DLL named like the mod wins; when
/// nothing can be picked with confidence the caller asks the user instead of guessing.
/// </summary>
public static class Me3NativeDll
{
    /// <summary>
    /// The DLL among <paramref name="relativePaths"/> that belongs to the mod called <paramref name="modId"/>: the one
    /// whose name matches the mod's, else the only DLL. Null when there is none or more than one candidate.
    /// </summary>
    public static string? Find(IEnumerable<string> relativePaths, string modId)
    {
        var dlls = ListDlls(relativePaths);
        if (dlls.Length == 0) return null;

        var wanted = Normalize(modId);
        var named = dlls.Where(dll => Normalize(Path.GetFileNameWithoutExtension(dll)) == wanted).ToArray();
        if (named.Length == 1) return named[0];

        return dlls.Length == 1 ? dlls[0] : null;
    }

    /// <summary>The DLLs among the paths, in ordinal order.</summary>
    public static string[] ListDlls(IEnumerable<string> relativePaths)
        => relativePaths
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();

    // Lowercase letters and digits only, so "Cool-Mod v2" and "cool_mod_v2.dll" are the same name.
    private static string Normalize(string name) => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
