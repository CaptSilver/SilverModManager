using NexusMods.Paths;

namespace SilverModManager.Sdk.Games;

/// <summary>
/// The one rule for files no game or script extender ever loads: the source and project files a mod author ships
/// next to the mod (SKSE's <c>src/</c> folder is the common case). They are left out wherever files are taken in
/// or compared, so they are never unpacked, installed or offered to the user as something that came from outside.
/// </summary>
/// <remarks>
/// Deliberately narrow. Anything a game or extender can load stays out of the rule (<c>.dll .exe .esp .esm .esl .bsa
/// .ba2 .pex</c>), and so do formats that are data in some game (<c>.ini .json .txt</c>) and Papyrus source (<c>.psc</c>),
/// which the Creation Kit reads. C# source and <c>.inc</c> only count below a source folder: some games compile loose C# mods, and shader packs ship <c>.inc</c> includes the game reads.
/// </remarks>
public static class NeverLoadedFiles
{
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cpp", ".c", ".cc", ".h", ".hpp", ".inl",
        ".vcxproj", ".vcproj", ".filters", ".sln", ".rc", ".cmake",
    };

    // Files that are source only inside a source tree: C# and .inc include files (data or mod code in some games), and build
    // and repository files (a linker .def, a .rc.in template, a .gitignore). Outside a source folder they are left alone.
    private static readonly HashSet<string> SourceTreeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".inc", ".def", ".in", ".props", ".targets", ".asm", ".natvis", ".gitignore", ".gitattributes", ".editorconfig", ".clang-format",
    };

    private static readonly HashSet<string> SourceFolders = new(StringComparer.OrdinalIgnoreCase) { "src", "source", "sources", "include" };

    private const string CMakeLists = "CMakeLists.txt";

    /// <summary>Whether <paramref name="path"/>, spelled with <c>/</c> or <c>\</c>, is a file no game loads.</summary>
    public static bool IsNeverLoaded(string path)
    {
        var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        var name = parts[^1];
        if (name.Equals(CMakeLists, StringComparison.OrdinalIgnoreCase)) return true;
        if (SourceExtensions.Contains(Path.GetExtension(name))) return true;
        return SourceTreeExtensions.Contains(Path.GetExtension(name)) && parts[..^1].Any(SourceFolders.Contains);
    }

    public static bool IsNeverLoaded(RelativePath path) => IsNeverLoaded(path.ToString());

    public static bool IsNeverLoaded(GamePath path) => IsNeverLoaded(path.Path);
}
