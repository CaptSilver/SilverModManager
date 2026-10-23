namespace SilverModManager.Games.Bethesda.Archives;

public static class BethesdaArchive
{
    /// <summary>
    /// Opens an archive from a seekable stream. The kind comes from the first four bytes only, never from a
    /// file name. The returned reader owns the stream.
    /// </summary>
    public static async Task<IBethesdaArchiveReader> Open(Stream stream, CancellationToken cancellationToken)
    {
        if (!stream.CanSeek) throw new ArgumentException("Archives are read by seeking; the stream must be seekable", nameof(stream));

        var magic = new byte[4];
        stream.Position = 0;
        var read = await stream.ReadAtLeastAsync(magic, magic.Length, throwOnEndOfStream: false, cancellationToken);
        if (read < magic.Length)
            throw new BethesdaArchiveException($"Not a Bethesda archive: the stream has {read} byte(s), too short for a header");

        try
        {
            if (magic.AsSpan().SequenceEqual(Tes3ArchiveReader.Magic))
                return Tes3ArchiveReader.Load(new ArchiveSource(stream, ArchiveKind.Tes3));
            if (magic.AsSpan().SequenceEqual(Tes5ArchiveReader.Magic))
                return Tes5ArchiveReader.Load(new ArchiveSource(stream, ArchiveKind.Tes5));
            if (magic.AsSpan().SequenceEqual(Ba2ArchiveReader.Magic))
                return Ba2ArchiveReader.Load(new ArchiveSource(stream, ArchiveKind.Ba2));
        }
        catch (EndOfStreamException e)
        {
            // Length checks run first, so this only fires if the stream shrinks while it is being read.
            throw new BethesdaArchiveException("Archive ended unexpectedly while its headers were being read", e);
        }

        throw new BethesdaArchiveException(
            $"Not a Bethesda archive: unknown magic {Convert.ToHexString(magic)}");
    }
}
