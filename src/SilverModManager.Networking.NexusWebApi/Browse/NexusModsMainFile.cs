using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Networking.NexusWebApi;

public static class NexusModsMainFile
{
    /// <summary>
    /// The file Install downloads: the mod's one file in the MAIN category. A mod with none, or with several (usually
    /// variants the user must choose between), has no such file, and the caller sends the user to the mod's Files tab.
    /// </summary>
    public static FileId? Pick(IEnumerable<NexusModsFileRow> files)
    {
        FileId? main = null;
        foreach (var file in files)
        {
            if (file.Category != NexusModsFileCategory.Main) continue;
            if (main is not null) return null;
            main = file.Id;
        }
        return main;
    }

    /// <summary>How many files are in the MAIN category, so a caller can say why <see cref="Pick"/> chose nothing.</summary>
    public static int MainCount(IEnumerable<NexusModsFileRow> files) => files.Count(static file => file.Category == NexusModsFileCategory.Main);
}
