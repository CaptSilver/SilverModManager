using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Abstractions.Library;

/// <summary>
/// What the Nexus Mods browse page and the CLI share to put a Nexus file into a loadout: which Nexus game a loadout
/// is, the mod's files, the library copy of a file and how to fetch it when there is none.
/// </summary>
public interface INexusModsInstaller
{
    /// <summary>
    /// The Nexus game of the loadout, or null when there is no such loadout or its game is not on Nexus Mods.
    /// <paramref name="sharedWith"/> names another game that has the same Nexus id (Skyrim SE and Skyrim VR), so a
    /// caller can say a mod is meant for both.
    /// </summary>
    NexusModsGameId? GameOf(LoadoutId loadout, out string? sharedWith);

    /// <summary>The mod's files in every category. A mod Nexus does not know has none. Throws when Nexus does not answer.</summary>
    Task<IReadOnlyList<NexusModsFileRow>> Files(NexusModsGameId game, ModId mod, CancellationToken token);

    /// <summary>The mod a file belongs to. Throws when Nexus does not know the file or does not answer.</summary>
    Task<ModId> ModOf(NexusModsGameId game, FileId file, CancellationToken token);

    /// <summary>
    /// The library's copy of the file, or null. Older downloads of one file can sit in the library more than once, so
    /// a copy already linked into the loadout wins (it may be enabled or just disabled); otherwise any copy installs
    /// the same files.
    /// </summary>
    LibraryItem.ReadOnly? FindInLibrary(NexusModsGameId game, ModId mod, FileId file, LoadoutId loadout);

    /// <summary>
    /// The file as an install item: the library copy when there is one, otherwise a download through the premium
    /// download API into the library, which only an account that may use it can run.
    /// </summary>
    ResolvedInstall Resolve(NexusModsGameId game, ModId mod, FileId file, string name, bool dependency, LoadoutId loadout);

    /// <summary>The file's page on Nexus Mods, where a free account starts its download with the site's one-click link.</summary>
    Uri DownloadPage(NexusModsGameId game, ModId mod, FileId file);
}
