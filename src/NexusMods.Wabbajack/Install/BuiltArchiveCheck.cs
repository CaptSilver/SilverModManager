using NexusMods.Games.Bethesda.Archives;
using NexusMods.Hashing.xxHash3;

namespace NexusMods.Wabbajack.Install;

/// <summary>
/// Reads a freshly built archive back and compares what it holds with what was put in. A list's recorded hash for an
/// input cannot be the reference: a re-encoded texture or a remapped text file is not byte-identical to what the author
/// had, so the reference is the hash of the bytes this install actually wrote for the input.
/// </summary>
internal static class BuiltArchiveCheck
{
    /// <param name="archive">The built archive, readable and seekable. Left open.</param>
    /// <param name="written">Folded entry path to the xxHash64 of the bytes written for that input.</param>
    public static async Task Verify(Stream archive, IReadOnlyDictionary<string, Hash> written, CancellationToken cancellationToken)
    {
        archive.Position = 0;
        await using var reader = await BethesdaArchive.Open(new LeaveOpenStream(archive), cancellationToken);
        var held = new HashSet<string>();
        foreach (var entry in reader.Entries)
        {
            held.Add(Fold(entry.Path.ToString()));

            // A BA2 texture is stored as chunks of pixel data under a rebuilt header, so it never reads back as the DDS it came from.
            if (entry.State is Ba2Dx10FileState) continue;

            var path = entry.Path.ToString();
            if (!written.TryGetValue(Fold(path), out var expected))
                throw new ArchiveMismatchException(path, $"the built archive holds '{path}', which no input was written for");

            using var hash = new Xx64HashingStream();
            await entry.CopyToAsync(hash, cancellationToken);
            if (hash.Hash != expected)
                throw new ArchiveMismatchException(path, $"'{path}' reads back from the built archive as different bytes than the file written for it");
        }

        // A writer that drops a member would otherwise pass: every entry it did write is correct.
        foreach (var path in written.Keys)
        {
            if (!held.Contains(path))
                throw new ArchiveMismatchException(path, $"'{path}' was written for the archive but the built archive does not hold it");
        }
    }

    public static string Fold(string path) => path.Replace('\\', '/').ToLowerInvariant();
}
