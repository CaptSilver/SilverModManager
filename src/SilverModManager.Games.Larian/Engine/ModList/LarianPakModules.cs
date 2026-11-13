using System.Collections.Concurrent;
using System.Xml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Larian.Engine.GameInstalled;
using SilverModManager.Games.Larian.BaldursGate3.Models;
using SilverModManager.Games.Larian.Engine.SortOrder;
using SilverModManager.Games.Larian.Engine.Utils.LsxXmlParsing;
using SilverModManager.Games.Larian.Engine.Utils.PakParsing;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Sdk.Hashes;
using DynamicData.Kernel;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Resources;
using Polly;

namespace SilverModManager.Games.Larian.Engine.ModList;

/// <summary>
/// Finds out which module each pak of the loadout is. A pak the app holds is read from the file store through
/// the same metadata pipeline the dependency check uses; a pak the game installed is read from the entry the
/// game once gave it, or from the pak on disk when it never had one (the game's paks are never backed up).
/// </summary>
public sealed class LarianPakModules
{
    private readonly IResourceLoader<FileIdentity, Outcome<LspkPackageFormat.PakMetaData>> _metadataPipeline;
    private readonly ILogger<LarianPakModules> _logger;
    private readonly LarianGameProfile _profile;

    // Whether each pak the game lists adventures for is one, by content. The sort order builds its rows without
    // waiting, so it asks here; the answer comes from the same metadata read the mod list is worked out from.
    private readonly ConcurrentDictionary<FileIdentity, bool> _adventures = new();

    // The result is null for a pak that cannot be read, so it is not opened again until its content changes.
    private readonly ConcurrentDictionary<(string Name, FileIdentity Identity), ModsettingsDocument.ModuleEntry?> _diskReads = new();

    public LarianPakModules(IServiceProvider provider, LarianGameProfile profile)
    {
        _profile = profile;
        _metadataPipeline = Pipelines.GetMetadataPipeline(provider);
        _logger = provider.GetRequiredService<ILogger<LarianPakModules>>();
    }

    /// <summary>
    /// Every pak in the Mods folder of the loadout, in the order of the sort order, each with the entry it would
    /// be written as.
    /// </summary>
    public async Task<IReadOnlyList<LarianPakCandidate>> Candidates(
        Loadout.ReadOnly loadout,
        ISortOrderVariety variety,
        SortOrderId sortOrderId,
        CancellationToken token = default)
    {
        var rows = variety.GetSortOrderItems(sortOrderId, loadout.Db)
            .OfType<LarianPakReactiveSortItem>()
            .OrderBy(row => row.SortIndex)
            .ToList();

        var candidates = new List<LarianPakCandidate>(rows.Count);
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            if (row.LoadoutData is not LarianPakLoadoutData data)
                throw new InvalidOperationException($"The pak order row `{row.DisplayName}` has no loadout data");
            var entry = data.IsGameInstalled
                ? ReadGameInstalled(loadout, data)
                : await ReadFromStore(data, token);
            var isAdventure = _profile.HasAdventureModules && IsAdventureType(entry);
            if (!data.IsGameInstalled) _adventures[data.Identity] = isAdventure;

            // The game's mod list names the campaign, so a campaign is never switched on from here.
            candidates.Add(new LarianPakCandidate(row.DisplayName, data.IsGameInstalled, row.IsActive && !isAdventure, entry, isAdventure));
        }

        return candidates;
    }

    /// <summary>
    /// The paks in the Mods folder whose name is not in <paramref name="known"/>, as candidates read from disk.
    /// These are paks nobody has taken into the loadout yet, the way mods set up by hand are on a first manage.
    /// </summary>
    public IReadOnlyList<LarianPakCandidate> PaksOnDiskNotIn(Loadout.ReadOnly loadout, IReadOnlySet<string> known)
    {
        var folder = loadout.InstallationInstance.Locations.ToAbsolutePath(new GamePath(LarianConstants.ModsLocationId, ""));
        if (!folder.DirectoryExists()) return [];

        return Directory.EnumerateFiles(folder.ToString(), "*" + LarianConstants.PakFileExtension, SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => !known.Contains(name))
            .Order(StringComparer.Ordinal)
            .Select(name =>
            {
                var entry = ReadFromDisk(loadout, name, _logger);
                return new LarianPakCandidate(name, IsGameInstalled: false, IsActive: false, entry, IsAdventure: _profile.HasAdventureModules && IsAdventureType(entry));
            })
            .ToList();
    }

    private static bool IsAdventureType(ModsettingsDocument.ModuleEntry? entry) =>
        entry is not null && entry.Type.Equals("Adventure", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the pak is a campaign of a game whose mod list separates those. A game without any, and a pak the
    /// game installed, answer without reading anything; otherwise the pak's metadata is read once and kept.
    /// </summary>
    public bool IsAdventure(LarianPakLoadoutData data)
    {
        if (!_profile.HasAdventureModules || data.IsGameInstalled) return false;
        return _adventures.GetOrAdd(data.Identity, ReadIsAdventure);
    }

    // Blocks on the metadata pipeline, which keeps what it read in memory: only the first row of a pak waits. The
    // load runs on the thread pool so a caller on a thread with a synchronization context cannot deadlock on it.
    private bool ReadIsAdventure(FileIdentity identity)
    {
        try
        {
            var resource = Task.Run(async () => await _metadataPipeline.LoadResourceAsync(identity, CancellationToken.None)).GetAwaiter().GetResult();
            return resource.Data.Exception is null
                   && _profile.PakVersions.Contains(resource.Data.Result.PakVersion)
                   && IsAdventureType(ToEntry(resource.Data.Result.MetaFileData.ModuleShortDesc));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Unable to tell whether the pak {Identity} is an adventure", identity);
            return false;
        }
    }

    private async Task<ModsettingsDocument.ModuleEntry?> ReadFromStore(LarianPakLoadoutData data, CancellationToken token)
    {
        try
        {
            var resource = await _metadataPipeline.LoadResourceAsync(data.Identity, token);
            if (resource.Data.Exception is { } error)
            {
                _logger.LogWarning(error, "Unable to read the module of `{Name}`, it is left out of the mod list", data.Name);
                return null;
            }

            // A pak of another game's format would make the game refuse its mod list, so it is left out, and the
            // dependency check names the game it belongs to.
            if (LarianPakVersions.WhyForeign(_profile, resource.Data.Result.PakVersion) is { } foreign)
            {
                _logger.LogWarning("`{Name}` is not a pak this game loads, it is left out of the mod list. {Why}", data.Name, foreign);
                return null;
            }

            return ToEntry(resource.Data.Result.MetaFileData.ModuleShortDesc);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Exception while getting the module of `{Name}`", data.Name);
            return null;
        }
    }

    private ModsettingsDocument.ModuleEntry? ReadGameInstalled(Loadout.ReadOnly loadout, LarianPakLoadoutData data)
    {
        if (data.ModGroupId is { } itemId
            && GameInstalledItem.Load(loadout.Db, itemId) is { } item && item.IsValid()
            && item.TryGetAsBg3ModuleEntry(out var stored)
            && stored.Uuid.HasValue)
        {
            return new ModsettingsDocument.ModuleEntry(
                Folder: Or(stored.Folder, ""),
                Name: Or(stored.ModuleName, ""),
                PublishHandle: Or(stored.PublishHandle, "0"),
                Uuid: stored.Uuid.Value,
                Version64: Or(stored.Version64, ""),
                Md5: Or(stored.Md5, ""));
        }

        return _diskReads.GetOrAdd((data.Name, data.Identity), static (_, state) => ReadFromDisk(state.loadout, state.data.Name, state.logger), (loadout, data, logger: _logger));
    }

    private static string Or(Optional<string> value, string fallback) => value.HasValue ? value.Value : fallback;

    private static ModsettingsDocument.ModuleEntry? ReadFromDisk(Loadout.ReadOnly loadout, string name, ILogger logger)
    {
        try
        {
            using var stream = loadout.InstallationInstance.Locations
                .ToAbsolutePath(new GamePath(LarianConstants.ModsLocationId, name))
                .Open(FileMode.Open, FileAccess.Read, FileShare.Read);
            return ToEntry(PakFileParser.ParsePakMeta(stream).MetaFileData.ModuleShortDesc);
        }
        catch (Exception e) when (e is InvalidDataException or XmlException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Unable to read the module of the pak `{Name}` on disk", name);
            return null;
        }
    }

    /// <summary>
    /// A pak's own description of itself as a mod list entry. The game fills in the checksum of a module when it
    /// saves its list, and a missing handle means a locally installed module.
    /// </summary>
    private static ModsettingsDocument.ModuleEntry? ToEntry(LsxXmlFormat.ModuleShortDesc module)
    {
        if (string.IsNullOrEmpty(module.Uuid)) return null;
        return new ModsettingsDocument.ModuleEntry(
            Folder: module.Folder ?? "",
            Name: module.Name ?? "",
            PublishHandle: string.IsNullOrEmpty(module.PublishHandle) ? "0" : module.PublishHandle,
            Uuid: module.Uuid,
            Version64: module.Version ?? "",
            Md5: "",
            Type: module.Type ?? "");
    }
}
