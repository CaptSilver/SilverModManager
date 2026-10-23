using NexusMods.Paths;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// Text and names shared by the write-guard in the synchronizer and the diagnostics that report the same
/// problem, so the player reads one sentence for one cause.
/// </summary>
public static class ProtonMessages
{
    /// <summary>
    /// Name of the zero-byte file the synchronizer creates and deletes to find out whether a folder is
    /// writable. The base path filter excludes it, so a copy left behind by a crash is never indexed.
    /// </summary>
    public const string ProbeFileName = ".silvermodmanager-write-probe";

    /// <summary>
    /// The game runs under Proton but Proton has not created its prefix yet.
    /// </summary>
    public static string MissingPrefix(string gameName, AbsolutePath prefixDirectory)
        => $"{gameName} has no Proton prefix at '{prefixDirectory}' yet. Please launch the game once from Steam so Proton creates its prefix, then try again.";

    /// <summary>
    /// A folder the synchronizer has to write into cannot be written.
    /// </summary>
    public static string UnwritableFolder(AbsolutePath folder)
        => $"The folder '{folder}' cannot be written to, so nothing was changed. Check its permissions and try again.";
}
