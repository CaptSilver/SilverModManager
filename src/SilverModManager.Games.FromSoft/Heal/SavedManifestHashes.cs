using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using SilverModManager.Abstractions.Steam;
using SilverModManager.Abstractions.Steam.Values;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Games.FromSoft.Heal;

/// <summary>A manifest the app has kept the file list of. <paramref name="BuildId"/> is known only for a manifest that was installed when it was saved.</summary>
public sealed record SavedManifest(string AppId, string? BuildId, DepotId DepotId, ManifestId ManifestId, DateTimeOffset FirstSeen, int FileCount);

/// <summary>
/// The file lists of Steam depot manifests, copied out of Steam's depotcache into the app's own folder as
/// <c>&lt;depot&gt;_&lt;manifest&gt;.json</c>, one file per manifest. Steam drops the manifests of old builds from its cache
/// when it updates a game, and with them the only record of what that build's files hashed to; a manifest that was
/// read once keeps vouching for its files from here. A saved manifest is never rewritten, so its first-seen time stays.
/// Nothing is ever written to Steam's folders.
/// </summary>
/// <remarks>
/// This is a file under the FromSoft data root, like the baselines it vouches for, and not a database model: the local
/// baseline in the hashes database is rebuilt or removed with the install it describes, which is the opposite of what
/// this record is for, and a saved manifest needs no query beyond "which manifest lists this hash".
/// </remarks>
/// <param name="root">The folder the manifests are saved in. Resolved on every call, so a changed data root is followed.</param>
public sealed class SavedManifestHashes(Func<AbsolutePath> root, TimeProvider? time = null, ILogger? logger = null)
{
    private const string Extension = ".json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    private readonly object _gate = new();
    private readonly Dictionary<string, Loaded> _loaded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unreadable = new(StringComparer.Ordinal);

    private sealed record Loaded(SavedManifest Manifest, HashSet<Sha1Value> Hashes);

    private sealed class Record
    {
        public string AppId { get; set; } = "";
        public string? BuildId { get; set; }
        public uint DepotId { get; set; }
        public ulong ManifestId { get; set; }
        public DateTimeOffset FirstSeen { get; set; }
        public List<RecordFile> Files { get; set; } = [];
    }

    private sealed class RecordFile
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Path { get; set; }
        public ulong Size { get; set; }
        public string Sha1 { get; set; } = "";
    }

    /// <summary>
    /// Saves every manifest <paramref name="source"/> can read for the install that is not saved yet: the install's own,
    /// then (when the source can list them) every other one Steam kept for the same depots. Manifests that are saved
    /// already are not read again. Returns how many were newly saved. Anything unreadable is logged and skipped.
    /// </summary>
    public int Capture(IDepotManifestSource? source, GameInstallation installation)
    {
        var locator = installation.LocatorResult;
        if (source is null || locator.Store != GameStore.Steam || locator.StoreRoot is not { } storeRoot) return 0;

        var saved = 0;
        var tried = new HashSet<string>(StringComparer.Ordinal);
        var depots = new List<DepotId>();
        foreach (var id in locator.LocatorIds.Select(i => i.Value).Distinct(StringComparer.Ordinal))
        {
            if (!ulong.TryParse(id, out var value) || !source.TryGetManifestPath(storeRoot, ManifestId.From(value), locator.Path, out var path)) continue;
            if (!tried.Add(path.ToString())) continue;

            if (IsSaved(ManifestId.From(value))) AddDepotOf(path, depots);
            else if (SaveFrom(source, path, locator.StoreIdentifier, BuildIdOf(locator)) is { } list)
            {
                saved++;
                if (!depots.Contains(list.DepotId)) depots.Add(list.DepotId);
            }
        }

        if (source is not IDepotManifestCatalog catalog) return saved;
        foreach (var depot in depots)
        {
            foreach (var path in Cached(catalog, storeRoot, locator.Path, depot))
            {
                if (!tried.Add(path.ToString())) continue;
                if (NameOf(path) is { } name && IsSaved(name.Manifest)) continue;
                // An older manifest's build is not the installed one, and Steam does not record which it was.
                if (SaveFrom(source, path, locator.StoreIdentifier, buildId: null) is not null) saved++;
            }
        }
        return saved;
    }

    /// <summary>The manifest of <paramref name="appId"/> that lists a file with this SHA-1, or null when no saved one does. The oldest saved manifest answers.</summary>
    public ManifestMatch? Find(string appId, Sha1Value hash)
    {
        lock (_gate)
        {
            Refresh();
            return _loaded.Values
                .Where(l => l.Manifest.AppId == appId && l.Hashes.Contains(hash))
                .OrderBy(l => l.Manifest.FirstSeen).ThenBy(l => l.Manifest.ManifestId.Value)
                .Select(l => new ManifestMatch(l.Manifest.ManifestId, l.Manifest.DepotId))
                .FirstOrDefault();
        }
    }

    /// <summary>Every saved manifest, in the order they were first seen.</summary>
    public IReadOnlyList<SavedManifest> Saved()
    {
        lock (_gate)
        {
            Refresh();
            return _loaded.Values.Select(l => l.Manifest).OrderBy(m => m.FirstSeen).ThenBy(m => m.ManifestId.Value).ToArray();
        }
    }

    /// <summary>The saved file list of a manifest, empty when it is not saved or its file cannot be read.</summary>
    public IReadOnlyList<ManifestFile> Files(ManifestId manifestId)
    {
        var file = FileOf(manifestId);
        if (file is null) return [];
        try
        {
            var record = Read(file);
            return record.Files.Select(f => new ManifestFile(f.Path, f.Size, Sha1Value.FromHex(f.Sha1))).ToArray();
        }
        catch (Exception e) when (e is IOException or JsonException or FormatException or ArgumentException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "The saved manifest {File} could not be read", file);
            return [];
        }
    }

    private bool IsSaved(ManifestId manifestId) => FileOf(manifestId) is not null;

    private string? FileOf(ManifestId manifestId)
    {
        var folder = root().GetFullPath();
        if (!Directory.Exists(folder)) return null;
        return Directory.EnumerateFiles(folder, "*_" + manifestId.Value.ToString(CultureInfo.InvariantCulture) + Extension).FirstOrDefault();
    }

    private void AddDepotOf(AbsolutePath path, List<DepotId> depots)
    {
        if (NameOf(path) is { } name && !depots.Contains(name.Depot)) depots.Add(name.Depot);
    }

    /// <summary>The depot and manifest ids a depotcache file name carries (<c>&lt;depot&gt;_&lt;manifest&gt;.manifest</c>), or null for any other name.</summary>
    private static (DepotId Depot, ManifestId Manifest)? NameOf(AbsolutePath path)
    {
        var parts = System.IO.Path.GetFileNameWithoutExtension(path.FileName).Split('_');
        return parts is [var depot, var manifest] && uint.TryParse(depot, out var d) && ulong.TryParse(manifest, out var m)
            ? (DepotId.From(d), ManifestId.From(m))
            : null;
    }

    private static string? BuildIdOf(GameLocatorResult locator) => string.IsNullOrEmpty(locator.StoreBuildId) ? null : locator.StoreBuildId;

    private ManifestFileList? SaveFrom(IDepotManifestSource source, AbsolutePath path, string appId, string? buildId)
    {
        var list = ListOf(source, path);
        if (list is null) return null;

        try
        {
            return Write(list, appId, buildId) ? list : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "The file list of manifest {Manifest} could not be saved", list.ManifestId.Value);
            return null;
        }
    }

    private ManifestFileList? ListOf(IDepotManifestSource source, AbsolutePath path)
    {
        if (source is IDepotManifestCatalog catalog)
        {
            var list = catalog.ReadFileList(path);
            if (list is null) Warn(path, "is missing, truncated or not a manifest");
            return list;
        }

        switch (source.Read(path))
        {
            case LocalManifestResult.Parsed parsed:
                return new ManifestFileList(parsed.Manifest.DepotId, parsed.Manifest.ManifestId,
                    parsed.Manifest.Files.Select(f => new ManifestFile(f.Path.ToString(), f.Size.Value, f.Hash)).ToArray());
            case LocalManifestResult.NeedsDepotKey:
                _logger.LogDebug("Manifest {Path} has encrypted file names and this source cannot read its hashes without the depot key", path);
                return null;
            case LocalManifestResult.Unreadable unreadable:
                Warn(path, unreadable.Reason);
                return null;
            default:
                throw new NotSupportedException($"Unhandled manifest result for {path}");
        }
    }

    private void Warn(AbsolutePath path, string reason)
    {
        // A bad file stays in Steam's cache, so every pass would meet it again.
        lock (_gate)
        {
            if (!_warned.Add(path.ToString())) return;
        }
        _logger.LogWarning("Manifest {Path} was not saved: {Reason}", path, reason);
    }

    /// <summary>Writes the manifest unless one with its id is saved already. Returns whether a file was written.</summary>
    private bool Write(ManifestFileList list, string appId, string? buildId)
    {
        lock (_gate)
        {
            if (IsSaved(list.ManifestId)) return false;

            var folder = root();
            folder.CreateDirectory();
            var record = new Record
            {
                AppId = appId,
                BuildId = buildId,
                DepotId = list.DepotId.Value,
                ManifestId = list.ManifestId.Value,
                FirstSeen = _time.GetUtcNow(),
                Files = list.Files.Select(f => new RecordFile { Path = f.Path, Size = f.Size, Sha1 = f.Hash.ToString() }).ToList(),
            };

            var target = (folder / $"{record.DepotId}_{record.ManifestId}{Extension}").GetFullPath();
            var temp = target + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(record, Json));
            File.Move(temp, target, overwrite: true);
            return true;
        }
    }

    private static Record Read(string file) => JsonSerializer.Deserialize<Record>(File.ReadAllText(file), Json) ?? throw new JsonException("empty file");

    /// <summary>Loads the saved files that are not in memory yet. A file that cannot be read is logged once and left out.</summary>
    private void Refresh()
    {
        var folder = root().GetFullPath();
        if (!Directory.Exists(folder)) return;

        foreach (var file in Directory.EnumerateFiles(folder, "*" + Extension))
        {
            if (_loaded.ContainsKey(file) || _unreadable.Contains(file)) continue;
            try
            {
                var record = Read(file);
                var manifest = new SavedManifest(record.AppId, record.BuildId, DepotId.From(record.DepotId), ManifestId.From(record.ManifestId), record.FirstSeen, record.Files.Count);
                _loaded[file] = new Loaded(manifest, record.Files.Select(f => Sha1Value.FromHex(f.Sha1)).ToHashSet());
            }
            catch (Exception e) when (e is IOException or JsonException or FormatException or ArgumentException or UnauthorizedAccessException)
            {
                _logger.LogWarning(e, "The saved manifest {File} could not be read and is ignored", file);
                _unreadable.Add(file);
            }
        }
    }

    private IReadOnlyList<AbsolutePath> Cached(IDepotManifestCatalog catalog, AbsolutePath storeRoot, AbsolutePath installPath, DepotId depot)
    {
        try
        {
            return catalog.CachedManifests(storeRoot, installPath, depot);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "The cached manifests of depot {Depot} could not be listed", depot.Value);
            return [];
        }
    }
}
