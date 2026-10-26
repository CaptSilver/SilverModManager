namespace SilverModManager.Networking.NexusWebApi;

/// <summary>An uploaded Nexus Mods archive found by its MD5.</summary>
/// <param name="Md5">Lowercase hex.</param>
/// <param name="FileName">The archive's name on Nexus Mods.</param>
/// <param name="GameId">The numeric Nexus Mods game id.</param>
public sealed record ArchiveHash(string Md5, string FileName, uint GameId, uint ModId, uint FileId);

/// <summary>Helpers for the batched <c>fileHashes</c> lookup, which only knows whole uploaded archives.</summary>
public static class ArchiveHashes
{
    /// <summary>The hashes lowercased, each once, in groups of at most <paramref name="size"/>.</summary>
    public static IReadOnlyList<string[]> Batch(IEnumerable<string> md5s, int size)
        => md5s.Select(h => h.ToLowerInvariant()).Distinct().Chunk(size).ToArray();

    /// <summary>Whether a hash hit is an uploaded mod archive (the lookup also knows other kinds of file).</summary>
    public static bool IsModArchive(string fileType) => fileType.Equals("mod_file", StringComparison.OrdinalIgnoreCase);
}
