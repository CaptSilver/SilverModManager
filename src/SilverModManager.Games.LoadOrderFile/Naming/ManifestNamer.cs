using System.Collections.Concurrent;
using NexusMods.Paths;
using SilverModManager.Games.LoadOrderFile.Manifests;

namespace SilverModManager.Games.LoadOrderFile.Naming;

/// <summary>
/// Names a folder by the id in its manifest, and names nothing when the folder has no readable manifest (a workshop
/// item that is still downloading). The status line renders the order file again and again, so a manifest is read
/// again only when its size or write time changed.
/// </summary>
public sealed class ManifestNamer(IModManifestReader reader) : IExternalModNamer
{
    private readonly ConcurrentDictionary<AbsolutePath, (long Size, DateTime Written, string? Id)> _cache = new();

    /// <summary>How many manifests were read from disk, for the test that pins the cache.</summary>
    internal int FileReads => _fileReads;

    private int _fileReads;

    public IReadOnlyList<string> Names(AbsolutePath modFolder)
    {
        var manifest = modFolder.Combine(reader.ManifestPath);
        if (!manifest.FileExists) return [];

        var info = manifest.FileInfo;
        var size = info.Size.Value;
        var written = info.LastWriteTimeUtc;
        if (_cache.TryGetValue(manifest, out var cached) && cached.Size == (long)size && cached.Written == written)
            return cached.Id is null ? [] : [cached.Id];

        Interlocked.Increment(ref _fileReads);
        var id = reader.Read(ReadAll(manifest))?.Id;
        _cache[manifest] = ((long)size, written, id);
        return id is null ? [] : [id];
    }

    private static byte[] ReadAll(AbsolutePath file)
    {
        using var stream = file.Read();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
