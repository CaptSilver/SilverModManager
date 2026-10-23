using SilverModManager.Abstractions.Loadouts.Synchronizers;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.Creations;

/// <summary>
/// Finds the Creations a Creation Engine game installed itself, from the game's own records: the content
/// catalog and the manifests in the Creations folder. The format is shared and only the locations differ, so
/// each game builds one instance, used for both the sync filter (<see cref="RecordPaths"/>) and recognition.
/// </summary>
/// <remarks>
/// A Creation is the game's own, so none of its items can be claimed. Starfield's Creations folder also holds
/// <c>.ckm</c> files (the user's own Creation Kit uploads), which are not records and are ignored.
/// </remarks>
public sealed class CreationsCatalogRecognizer : IGameInstalledContentRecognizer
{
    private const string ManifestExtension = ".manifest";

    private readonly GamePath _catalog;
    private readonly GamePath _manifestFolder;
    private readonly GamePath _dataFolder;
    private readonly string _source;

    public CreationsCatalogRecognizer(GamePath catalog, GamePath manifestFolder, GamePath dataFolder, string source)
    {
        _catalog = catalog;
        _manifestFolder = manifestFolder;
        _dataFolder = dataFolder;
        _source = source;
    }

    public IReadOnlyList<GamePath> RecordPaths => [_catalog, _manifestFolder];

    public ValueTask<GameInstalledRecognition> Recognize(
        GameInstallation installation,
        IReadOnlyDictionary<GamePath, SyncNodePart> diskState,
        CancellationToken token)
    {
        try
        {
            return ValueTask.FromResult(new GameInstalledRecognition(Read(installation, diskState, token), RecordsReadable: true));
        }
        catch (CreationsRecordException)
        {
            return ValueTask.FromResult(new GameInstalledRecognition([], RecordsReadable: false));
        }
        catch (IOException)
        {
            return ValueTask.FromResult(new GameInstalledRecognition([], RecordsReadable: false));
        }
    }

    private List<GameInstalledContent> Read(GameInstallation installation, IReadOnlyDictionary<GamePath, SyncNodePart> diskState, CancellationToken token)
    {
        var onDisk = DataFilesOnDisk(diskState);
        var items = new List<GameInstalledContent>();
        var catalogued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in ReadCatalog(installation))
        {
            token.ThrowIfCancellationRequested();
            foreach (var file in entry.Files) catalogued.Add(file);

            var present = PresentFiles(entry.Files, onDisk);
            if (present.Count == 0) continue;
            items.Add(new GameInstalledContent(_source, entry.Key, entry.Title, entry.Version, GameInstalledConfidence.GameRecord, CanBeClaimed: false, present));
        }

        foreach (var manifest in ReadManifests(installation))
        {
            token.ThrowIfCancellationRequested();
            if (manifest.Files.All(catalogued.Contains)) continue;

            var present = PresentFiles(manifest.Files, onDisk);
            if (present.Count == 0) continue;
            items.Add(new GameInstalledContent(_source, manifest.Id, manifest.Title, Version: null, GameInstalledConfidence.GameRecord, CanBeClaimed: false, present));
        }

        return items;
    }

    private IReadOnlyList<ContentCatalogEntry> ReadCatalog(GameInstallation installation)
    {
        if (!installation.Locations.ContainsKey(_catalog.LocationId)) return [];

        var file = installation.Locations.ToAbsolutePath(_catalog);
        if (!file.FileExists) return [];

        using var stream = file.Read();
        return ContentCatalogReader.Read(stream);
    }

    private IEnumerable<(string Id, string Title, IReadOnlyList<string> Files)> ReadManifests(GameInstallation installation)
    {
        if (!installation.Locations.ContainsKey(_manifestFolder.LocationId)) yield break;

        var folder = installation.Locations.ToAbsolutePath(_manifestFolder);
        if (!folder.DirectoryExists()) yield break;

        // Sorted so the items come out in the same order on every pass.
        foreach (var file in folder.EnumerateFiles().OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase))
        {
            var name = file.FileName;
            if (!name.EndsWith(ManifestExtension, StringComparison.OrdinalIgnoreCase)) continue;

            using var stream = file.Read();
            var files = CreationsManifestReader.Read(stream);
            yield return (CreationsManifestReader.IdOf(name), TitleOf(files, name), files);
        }
    }

    // The first plugin names the Creation; a manifest without one falls back to its first file, then its id.
    private static string TitleOf(IReadOnlyList<string> files, string manifestName)
    {
        var plugin = files.FirstOrDefault(f => f.EndsWith(".esm", StringComparison.OrdinalIgnoreCase)
            || f.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)
            || f.EndsWith(".esl", StringComparison.OrdinalIgnoreCase));
        var chosen = plugin ?? files.FirstOrDefault();
        return chosen is null ? CreationsManifestReader.IdOf(manifestName) : Path.GetFileNameWithoutExtension(chosen);
    }

    // Keyed by the path under the Data folder, case-insensitive: the records name files in lower case.
    private Dictionary<string, GamePath> DataFilesOnDisk(IReadOnlyDictionary<GamePath, SyncNodePart> diskState)
    {
        var result = new Dictionary<string, GamePath>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in diskState.Keys)
        {
            if (!path.InFolder(_dataFolder)) continue;
            result[path.Path.RelativeTo(_dataFolder.Path).ToString()] = path;
        }
        return result;
    }

    private static List<GamePath> PresentFiles(IEnumerable<string> names, Dictionary<string, GamePath> onDisk)
    {
        var present = new List<GamePath>();
        foreach (var name in names)
            if (onDisk.TryGetValue(name, out var path) && !present.Contains(path)) present.Add(path);
        return present;
    }
}
