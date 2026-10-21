using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using NexusMods.Paths;

namespace NexusMods.Games.FromSoft.Tests.Fixtures;

internal static class TestArchives
{
    /// <summary>Writes a zip at the path whose entries each hold their own path as content.</summary>
    public static async Task Write(AbsolutePath path, params string[] entryPaths)
    {
        await using var stream = path.Create();
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var entryPath in entryPaths)
        {
            await using var entry = zip.CreateEntry(entryPath).Open();
            await entry.WriteAsync(Encoding.UTF8.GetBytes(entryPath));
        }
    }

    /// <summary>
    /// Writes a gzipped tar at the path with one entry per path, each holding its own path as content. The names are
    /// stored exactly as given, so a leading "./" stays in the archive.
    /// </summary>
    public static async Task WriteTarGz(AbsolutePath path, params string[] entryPaths)
    {
        await using var stream = path.Create();
        await using var gzip = new GZipStream(stream, CompressionLevel.Optimal);
        await using var tar = new TarWriter(gzip, TarEntryFormat.Ustar, leaveOpen: false);
        foreach (var entryPath in entryPaths)
        {
            var entry = new UstarTarEntry(TarEntryType.RegularFile, entryPath)
            {
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(entryPath)),
            };
            await tar.WriteEntryAsync(entry);
        }
    }
}
