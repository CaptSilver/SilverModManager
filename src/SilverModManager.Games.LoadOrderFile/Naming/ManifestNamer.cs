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
    private readonly ConcurrentDictionary<AbsolutePath, (long Size, DateTime Written, ModManifest? Manifest)> _cache = new();

    /// <summary>How many manifests were read from disk, for the test that pins the cache.</summary>
    internal int FileReads => _fileReads;

    private int _fileReads;

    public IReadOnlyList<string> Names(AbsolutePath modFolder) => Manifest(modFolder)?.Id is { } id ? [id] : [];

    /// <summary>The manifest <paramref name="modFolder"/> carries, or null when it has none or it cannot be read.</summary>
    public ModManifest? Manifest(AbsolutePath modFolder)
    {
        var manifest = modFolder.Combine(reader.ManifestPath);
        if (!manifest.FileExists) return null;

        var info = manifest.FileInfo;
        var size = (long)info.Size.Value;
        var written = info.LastWriteTimeUtc;
        if (_cache.TryGetValue(manifest, out var cached) && cached.Size == size && cached.Written == written) return cached.Manifest;

        Interlocked.Increment(ref _fileReads);
        var read = reader.Read(ReadAll(manifest));
        _cache[manifest] = (size, written, read);
        return read;
    }

    internal static byte[] ReadAll(AbsolutePath file)
    {
        using var stream = file.Read();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
