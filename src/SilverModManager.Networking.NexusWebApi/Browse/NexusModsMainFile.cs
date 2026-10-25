using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Networking.NexusWebApi;

/// <summary>A file of a mod, reduced to what picking its main file needs.</summary>
public readonly record struct NexusModsFileSummary(FileId Id, ModFileCategory Category);

internal static class NexusModsMainFile
{
    /// <summary>
    /// The file Install downloads: the mod's one file in the MAIN category. A mod with none, or with several (usually
    /// variants the user must choose between), has no such file, and the caller sends the user to the mod's Files tab.
    /// </summary>
    public static FileId? Pick(IEnumerable<NexusModsFileSummary> files)
    {
        FileId? main = null;
        foreach (var file in files)
        {
            if (file.Category != ModFileCategory.Main) continue;
            if (main is not null) return null;
            main = file.Id;
        }
        return main;
    }
}
