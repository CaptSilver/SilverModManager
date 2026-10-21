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
}
