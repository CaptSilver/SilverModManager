using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.Larian.BaldursGate3.Utils;
using NexusMods.Games.Larian.BaldursGate3.Utils.LsxXmlParsing;
using NexusMods.Games.Larian.BaldursGate3.Utils.PakParsing;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using System.Collections.Concurrent;
using System.Xml;

namespace NexusMods.Games.Larian.BaldursGate3.GameInstalled;

/// <summary>
/// Finds the paks the game downloaded from mod.io itself. A pak is the game's when a `modsettings.lsx` entry
/// with a mod.io handle names its folder, or when it has the game's file naming and its own `meta.lsx`
/// carries a handle. Anything else in the Mods folder is the user's.
/// </summary>
public sealed class Bg3InGameModRecognizer : IGameInstalledContentRecognizer
{
    public const string SourceName = "Bg3InGameMod";

    private static readonly GamePath ModsettingsPath = new(Bg3Constants.PlayerProfilesLocationId, "modsettings.lsx");

    // The result is null for a pak that is unreadable or has no handle, so those are not opened every sync either.
    private readonly ConcurrentDictionary<GamePath, (ulong Size, long Ticks, PakIdentity? Identity)> _metaCache = new();
    private int _metaReads;

    private sealed record PakIdentity(string Name);

    /// <summary>How many paks have been opened so far. Lets tests show that a cached lookup reads nothing.</summary>
    public int MetaReads => Volatile.Read(ref _metaReads);

    /// <summary>
    /// Empty: `modsettings.lsx` is read as a record but stays an ordinary indexed file, because the load order
    /// writes it.
    /// </summary>
    public IReadOnlyList<GamePath> RecordPaths => [];

    public ValueTask<GameInstalledRecognition> Recognize(
        GameInstallation installation,
        IReadOnlyDictionary<GamePath, SyncNodePart> diskState,
        CancellationToken token)
    {
        IReadOnlyList<ModsettingsDocument.ModuleEntry> published = [];
        if (diskState.ContainsKey(ModsettingsPath))
        {
            try
            {
                var xml = File.ReadAllText(installation.Locations.ToAbsolutePath(ModsettingsPath).ToString());
                published = ModsettingsDocument.Parse(xml).Entries.Where(e => e.IsPublished).ToList();
            }
            catch (Exception e) when (e is XmlException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return ValueTask.FromResult(new GameInstalledRecognition([], RecordsReadable: false));
            }
        }

        var items = new List<GameInstalledContent>();
        var candidates = diskState
            .Where(kv => IsCandidate(kv.Key))
            .OrderBy(kv => kv.Key.Path.Path, StringComparer.Ordinal);

        foreach (var (path, part) in candidates)
        {
            token.ThrowIfCancellationRequested();

            var stem = path.FileName.ToString()[..^Bg3Constants.PakFileExtension.ToString().Length];
            var fileName = path.FileName.ToString();

            var entry = FindEntry(stem, published);
            if (entry is not null)
            {
                items.Add(new GameInstalledContent(
                    SourceName, fileName, entry.Name, RenderVersion(entry.Version64),
                    GameInstalledConfidence.GameRecord, CanBeClaimed: true, [path], entry));
                continue;
            }

            if (!PakNameMatch.LooksInGame(stem)) continue;

            var identity = ReadPublishedIdentity(installation, path, part);
            if (identity is null) continue;

            items.Add(new GameInstalledContent(
                SourceName, fileName, identity.Name, Version: null,
                GameInstalledConfidence.FileNamePattern, CanBeClaimed: true, [path]));
        }

        return ValueTask.FromResult(new GameInstalledRecognition(items, RecordsReadable: true));
    }

    private static bool IsCandidate(GamePath path)
        => path.LocationId == Bg3Constants.ModsLocationId
           && path.Path.Parent.Equals(RelativePath.Empty)
           && path.Extension == Bg3Constants.PakFileExtension;

    /// <summary>
    /// An entry naming the whole folder beats one the head only starts. Two entries that tie in the same way
    /// leave the pak without a record: the file name does not say which of them it is.
    /// </summary>
    private static ModsettingsDocument.ModuleEntry? FindEntry(string stem, IReadOnlyList<ModsettingsDocument.ModuleEntry> published)
    {
        if (!PakNameMatch.TrySplit(stem, out var head)) return null;

        var matches = published.Where(e => PakNameMatch.MatchesFolder(stem, e.Folder)).ToList();
        var exact = matches.Where(e => e.Folder.ToLowerInvariant() == head).ToList();
        var best = exact.Count > 0 ? exact : matches;
        return best.Count == 1 ? best[0] : null;
    }

    /// <summary>Dotted digits from a `Version64`, or null when it is not a number.</summary>
    private static string? RenderVersion(string version64)
        => ulong.TryParse(version64, out _) ? LsxXmlFormat.ModuleVersion.FromInt64String(version64).ToString() : null;

    private PakIdentity? ReadPublishedIdentity(GameInstallation installation, GamePath path, SyncNodePart part)
    {
        var ticks = part.LastModifiedTicks;
        var size = part.Size.Value;
        if (_metaCache.TryGetValue(path, out var cached) && cached.Size == size && cached.Ticks == ticks)
            return cached.Identity;

        Interlocked.Increment(ref _metaReads);
        PakIdentity? identity = null;
        try
        {
            using var stream = installation.Locations.ToAbsolutePath(path).Open(FileMode.Open, FileAccess.Read, FileShare.Read);
            var module = PakFileParser.ParsePakMeta(stream).MetaFileData.ModuleShortDesc;
            if (ModsettingsDocument.IsPublishHandle(module.PublishHandle))
                identity = new PakIdentity(module.Name);
        }
        catch (Exception e) when (e is InvalidDataException or XmlException or IOException or UnauthorizedAccessException)
        {
            // An unreadable pak is the user's file, not the game's.
        }

        _metaCache[path] = (size, ticks, identity);
        return identity;
    }
}
