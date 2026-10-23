using System.Collections.Concurrent;

namespace SilverModManager.Games.FromSoft.Formats;

/// <summary>
/// Reads single files out of a game's packed archives. Read-only: nothing is written into the game folder, and the
/// payload file (<c>.bdt</c>) is never read whole.
/// </summary>
public static class GameArchives
{
    // Decrypting an index is thousands of modular exponentiations, around a second for a large one, and a merge asks
    // for more than one file. Keyed on the file's identity so a game update replaces the entry instead of serving a
    // stale index. The key and layout are part of it too: the same bytes read with another key must fail, not hit.
    private static readonly ConcurrentDictionary<(string Path, long Length, long Modified, ArchiveIndex Archive, Bhd5Layout Layout), Bhd5.Index> IndexCache = new();

    /// <summary>The archive and entry that hold <paramref name="relPath"/>, or null when no archive does.</summary>
    public static (ArchiveIndex Archive, Bhd5.Entry Entry)? Find(string gameFolder, string relPath, FromSoftFormatTraits traits)
    {
        var wanted = Bhd5.PathHash(Bhd5.Normalise(relPath), traits);
        foreach (var archive in traits.Archives)
        {
            // An archive this install does not ship is skipped; one that fails to decrypt is not, because a rotated
            // key would otherwise look like a file that is simply missing.
            if (LoadIndex(gameFolder, archive, traits) is { } index && index.Entries.TryGetValue(wanted, out var entry))
                return (archive, entry);
        }
        return null;
    }

    /// <summary>
    /// The bytes of one game-relative path as stored in the archive, usually a DCX. Throws on a miss rather than
    /// returning nothing: a merge that uses this as its base must never proceed from a file it failed to find.
    /// </summary>
    public static byte[] ReadFile(string gameFolder, string relPath, FromSoftFormatTraits traits)
    {
        var normalised = Bhd5.Normalise(relPath);
        if (traits.Archives.IsEmpty)
            throw new Bhd5Exception($"This game's files are not read from archives, so {normalised} cannot be");
        if (Find(gameFolder, relPath, traits) is not var (archive, entry))
            throw new Bhd5Exception($"{normalised} is not in any of {string.Join(", ", traits.Archives.Select(a => a.Name))}: check the spelling against the game's own layout, which is lowercase and slash-separated");

        if (entry.PaddedSize < 0)
            throw new Bhd5Exception($"{normalised} is recorded as {entry.PaddedSize} bytes in {archive.Name}.bhd");
        var bdt = Path.Combine(gameFolder, archive.Name + ".bdt");
        var blob = new byte[entry.PaddedSize];
        try
        {
            // Seek and take only the padded size: these files run to gigabytes.
            using var stream = new FileStream(bdt, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(entry.Offset, SeekOrigin.Begin);
            stream.ReadExactly(blob);
        }
        catch (EndOfStreamException)
        {
            throw new Bhd5Exception($"{archive.Name}.bdt ended early reading {normalised}: wanted {entry.PaddedSize} bytes at {entry.Offset}");
        }
        catch (IOException e)
        {
            throw new Bhd5Exception($"Cannot read {bdt}: {e.Message}");
        }

        DecryptRanges(blob, entry);
        // The unpadded size is 0 for entries the index does not track that closely; the padded buffer is then the best
        // answer there is, and a DCX carries its own length anyway.
        if (entry.UnpaddedSize == 0)
            return blob;
        if (entry.UnpaddedSize < 0 || entry.UnpaddedSize > blob.Length)
            throw new Bhd5Exception($"{normalised} is recorded as {entry.UnpaddedSize} bytes inside a {blob.Length}-byte slot in {archive.Name}.bhd");
        return blob[..entry.UnpaddedSize];
    }

    private static void DecryptRanges(byte[] blob, Bhd5.Entry entry)
    {
        if (entry.AesKey is null)
            return;
        foreach (var (start, end) in entry.Ranges)
        {
            // The cipher works on whole blocks, so widen the range outward to block edges.
            var lo = start - start % AesCbc.BlockSize;
            var hi = Math.Min(blob.Length, end + (AesCbc.BlockSize - end % AesCbc.BlockSize) % AesCbc.BlockSize);
            if (hi > lo)
                AesCbc.DecryptEcb(entry.AesKey, blob.AsSpan((int)lo, (int)(hi - lo))).CopyTo(blob.AsSpan((int)lo));
        }
    }

    private static Bhd5.Index? LoadIndex(string gameFolder, ArchiveIndex archive, FromSoftFormatTraits traits)
    {
        var path = Path.Combine(gameFolder, archive.Name + ".bhd");
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
                return null;
        }
        catch (IOException e)
        {
            throw new Bhd5Exception($"Cannot read {path}: {e.Message}");
        }

        var token = (path, info.Length, info.LastWriteTimeUtc.Ticks, archive, traits.ArchiveLayout);
        if (IndexCache.TryGetValue(token, out var cached))
            return cached;
        try
        {
            var parsed = Bhd5.Parse(Bhd5.DecryptHeader(File.ReadAllBytes(path), archive), traits);
            IndexCache[token] = parsed;
            return parsed;
        }
        catch (IOException e)
        {
            throw new Bhd5Exception($"Cannot read {path}: {e.Message}");
        }
    }
}
