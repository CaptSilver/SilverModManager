using System.Security.Cryptography;
using SilverModManager.Abstractions.Steam;
using SilverModManager.Abstractions.Steam.DTOs;
using SilverModManager.Abstractions.Steam.Values;
using NexusMods.Paths;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Games.FromSoft.Tests.Heal;

/// <summary>
/// Depot manifests that exist only in memory. A manifest is "cached" from the moment it is added. This is the plain
/// source: it can resolve a manifest id and read a manifest whose names are readable, nothing more.
/// </summary>
public class FakeManifestSource : IDepotManifestSource
{
    protected sealed record Entry(DepotId Depot, ManifestId Id, byte[][] Files, bool EncryptedNames);

    protected readonly Dictionary<string, Entry> ByPath = [];

    public List<ManifestId> Reads { get; } = [];

    public static Sha1Value Sha1(byte[] content) => Sha1Value.From(SHA1.HashData(content));

    public void Add(uint depot, ulong id, bool encryptedNames = false, params byte[][] files)
    {
        var path = $"/fake-depotcache/{depot}_{id}.manifest";
        ByPath[path] = new Entry(DepotId.From(depot), ManifestId.From(id), files, encryptedNames);
    }

    public bool TryGetManifestPath(AbsolutePath steamRoot, ManifestId manifestId, AbsolutePath? installPath, out AbsolutePath manifestPath)
    {
        var hit = ByPath.FirstOrDefault(e => e.Value.Id == manifestId);
        manifestPath = hit.Key is null ? default : FileSystem.Shared.FromUnsanitizedFullPath(hit.Key);
        return hit.Key is not null;
    }

    public LocalManifestResult Read(AbsolutePath manifestPath)
    {
        var entry = ByPath[manifestPath.ToString()];
        Reads.Add(entry.Id);
        if (entry.EncryptedNames) return new LocalManifestResult.NeedsDepotKey(entry.Depot, entry.Id);
        return new LocalManifestResult.Parsed(new Manifest
        {
            ManifestId = entry.Id,
            DepotId = entry.Depot,
            CreationTime = DateTimeOffset.UnixEpoch,
            TotalCompressedSize = Size.Zero,
            TotalUncompressedSize = Size.Zero,
            Files = entry.Files.Select((content, i) => new Manifest.FileData
            {
                Path = RelativePath.FromUnsanitizedInput($"file{i}.bin"),
                Size = Size.FromLong(content.Length),
                Hash = Sha1(content),
                Chunks = [],
            }).ToArray(),
        });
    }
}

/// <summary>The same manifests, listable by depot and readable for their hashes whether or not the names are encrypted.</summary>
public sealed class FakeManifestCatalog : FakeManifestSource, IDepotManifestCatalog
{
    public IReadOnlyList<AbsolutePath> CachedManifests(AbsolutePath steamRoot, AbsolutePath? installPath, DepotId depot)
        => ByPath.Where(e => e.Value.Depot == depot).OrderBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => FileSystem.Shared.FromUnsanitizedFullPath(e.Key)).ToArray();

    public ManifestFileHashes? ReadFileHashes(AbsolutePath manifestPath)
    {
        if (!ByPath.TryGetValue(manifestPath.ToString(), out var entry)) return null;
        Reads.Add(entry.Id);
        return new ManifestFileHashes(entry.Depot, entry.Id, entry.Files.Select(Sha1).ToHashSet());
    }
}
