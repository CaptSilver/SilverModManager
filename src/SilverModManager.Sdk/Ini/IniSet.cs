namespace SilverModManager.Sdk.Ini;

/// <summary>
/// The INI files a game reads, by file name and in the order it resolves them: a key set in a later file
/// overrides the same key in an earlier one (Skyrim reads <c>Skyrim.ini</c>, then <c>SkyrimCustom.ini</c>, then
/// <c>SkyrimPrefs.ini</c>). Where the files sit is the caller's business; the set only knows names and order.
/// </summary>
public sealed class IniSet(params string[] fileNames)
{
    public IReadOnlyList<string> FileNames { get; } = fileNames;

    /// <summary>
    /// The value the game ends up with for a key: the one from the last file that sets it, even when that value
    /// is empty. Null when no file sets it. <paramref name="read"/> returns the document of a file name, or null
    /// for a file that does not exist.
    /// </summary>
    public string? Resolve(string section, string key, Func<string, IniDocument?> read)
    {
        string? resolved = null;
        foreach (var name in FileNames)
            if (read(name)?.Get(section, key) is { } value) resolved = value;
        return resolved;
    }
}
