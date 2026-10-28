using SilverModManager.Sdk.Library;

namespace SilverModManager.Games.Witcher.Witcher3.Installers;

/// <summary>Spots the Witcher Script Merger tool in an archive, which the app replaces with its own merge on Apply.</summary>
internal static class ScriptMergerArchive
{
    private const string ExecutableName = "WitcherScriptMerger.exe";

    public static bool IsScriptMerger(IEnumerable<string> archivePaths) =>
        archivePaths.Any(path => path.Replace('\\', '/').Split('/')[^1].Equals(ExecutableName, StringComparison.OrdinalIgnoreCase));

    public static bool IsScriptMerger(LibraryArchive.ReadOnly archive) =>
        IsScriptMerger(archive.Children.Select(child => child.Path.Path));
}
