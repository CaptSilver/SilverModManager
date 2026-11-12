// Follows libloot src/archive/mod.rs (normalise_path, do_assets_overlap), bsa.rs and ba2.rs (read_assets).
using System.IO.Hashing;
using System.Text;

namespace SilverModManager.Games.Bethesda.Archives;

/// <summary>
/// The files an archive (or several) holds, as a set of 64-bit hashes of their normalised paths. LOOT only asks how
/// many assets a plugin loads and whether two plugins load the same one, so the names themselves are not kept.
/// </summary>
/// <remarks>
/// A path is compared the way libloot compares it: ASCII letters lowercased, <c>/</c> turned into <c>\</c>, and
/// leading and trailing backslashes dropped. Characters above 127 are left alone.
/// </remarks>
public sealed class ArchiveAssetSet
{
    public static ArchiveAssetSet Empty { get; } = new([]);

    // Sorted and without duplicates, so a count is a length and an overlap is one pass over both arrays.
    private readonly ulong[] _hashes;

    private ArchiveAssetSet(ulong[] hashes) => _hashes = hashes;

    /// <summary>How many distinct files the set holds.</summary>
    public int Count => _hashes.Length;

    public static ArchiveAssetSet FromPaths(IEnumerable<string> paths)
    {
        var hashes = paths.Select(Hash).ToArray();
        return FromHashes(hashes);
    }

    /// <summary>The files of every set together; a file two archives both hold counts once.</summary>
    public static ArchiveAssetSet Union(IEnumerable<ArchiveAssetSet> sets) => FromHashes(sets.SelectMany(s => s._hashes).ToArray());

    /// <summary>
    /// Lists the files of the archive in <paramref name="archive"/> and disposes the stream. A stream that cannot
    /// seek is copied to a temporary file first, since the readers jump around the archive's index.
    /// </summary>
    /// <exception cref="BethesdaArchiveException">The bytes are not a readable archive.</exception>
    public static async Task<ArchiveAssetSet> Read(Stream archive, CancellationToken cancellationToken)
    {
        await using var owned = archive;
        var seekable = archive.CanSeek ? archive : await Spool(archive, cancellationToken);

        IBethesdaArchiveReader reader;
        try
        {
            reader = await BethesdaArchive.Open(seekable, cancellationToken);
        }
        catch
        {
            if (!ReferenceEquals(seekable, archive)) await seekable.DisposeAsync();
            throw;
        }

        // The reader owns the stream it was opened over.
        await using (reader)
            return FromPaths(reader.Entries.Select(e => e.Path.ToString()));
    }

    /// <summary>True when both sets hold the same file.</summary>
    public bool Overlaps(ArchiveAssetSet other)
    {
        var left = 0;
        var right = 0;
        while (left < _hashes.Length && right < other._hashes.Length)
        {
            if (_hashes[left] == other._hashes[right]) return true;

            if (_hashes[left] < other._hashes[right]) left++;
            else right++;
        }

        return false;
    }

    private static ArchiveAssetSet FromHashes(ulong[] hashes)
    {
        Array.Sort(hashes);
        var unique = 0;
        for (var i = 0; i < hashes.Length; i++)
            if (i == 0 || hashes[i] != hashes[i - 1]) hashes[unique++] = hashes[i];

        return new ArchiveAssetSet(hashes[..unique]);
    }

    private static ulong Hash(string path) => XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(Normalise(path)));

    internal static string Normalise(string path)
    {
        var chars = path.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = chars[i] switch
            {
                '/' => '\\',
                >= 'A' and <= 'Z' => (char)(chars[i] + ('a' - 'A')),
                _ => chars[i],
            };
        }

        return new string(chars).Trim('\\');
    }

    private static async Task<Stream> Spool(Stream archive, CancellationToken cancellationToken)
    {
        var spool = new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None,
            81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            await archive.CopyToAsync(spool, cancellationToken);
            spool.Position = 0;
            return spool;
        }
        catch
        {
            await spool.DisposeAsync();
            throw;
        }
    }
}
