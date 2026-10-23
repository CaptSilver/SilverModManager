using System.Xml;
using Microsoft.Extensions.Logging;
using NexusMods.Games.Larian.BaldursGate3.Utils.LsxXmlParsing;
using NexusMods.Games.Larian.BaldursGate3.Utils.PakParsing;
using NexusMods.Paths;

namespace NexusMods.Games.Larian.BaldursGate3.Utils;

/// <summary>
/// The modules the installed game ships (Gustav, GustavX, PhotoMode, ...), read from the
/// `Mods/&lt;Folder&gt;/meta.lsx` entries of the paks in the game's Data folder. Mods list these as
/// dependencies without anything in the loadout providing them, and the set changes with game patches.
/// </summary>
public sealed class GameModuleCatalog(ILogger<GameModuleCatalog> logger)
{
    private readonly object _lock = new();
    private readonly Dictionary<AbsolutePath, (string Fingerprint, IReadOnlyDictionary<string, LsxXmlFormat.ModuleShortDesc> Modules)> _cache = new();
    private int _pakReads;

    /// <summary>How many paks have been opened so far. Lets tests show that a cached lookup reads nothing.</summary>
    public int PakReads => Volatile.Read(ref _pakReads);

    /// <summary>
    /// The modules the paks in <paramref name="dataFolder"/> provide, keyed by module UUID, or null when the
    /// folder is missing or no pak holds a module (the game cannot be read).
    /// A folder is read again only when the name, size or write time of one of its paks changed.
    /// </summary>
    public IReadOnlyDictionary<string, LsxXmlFormat.ModuleShortDesc>? GetModules(AbsolutePath dataFolder)
    {
        if (!dataFolder.DirectoryExists()) return null;

        var paks = dataFolder.EnumerateFiles(Bg3Constants.PakFileExtension, recursive: false)
            .OrderBy(p => p.FileName.ToString(), StringComparer.Ordinal)
            .ToArray();
        var pakNames = paks.Select(p => p.FileName.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        paks = paks.Where(p => !IsArchivePart(p.FileName.ToString(), pakNames)).ToArray();
        var fingerprint = string.Join('|', paks.Select(p => $"{p.FileName}:{p.FileInfo.Size.Value}:{p.FileInfo.LastWriteTimeUtc.Ticks}"));

        lock (_lock)
        {
            if (_cache.TryGetValue(dataFolder, out var cached) && cached.Fingerprint == fingerprint)
                return cached.Modules.Count == 0 ? null : cached.Modules;

            var modules = new Dictionary<string, LsxXmlFormat.ModuleShortDesc>(StringComparer.OrdinalIgnoreCase);
            foreach (var pak in paks)
            {
                Interlocked.Increment(ref _pakReads);
                try
                {
                    using var stream = pak.Read();
                    foreach (var module in PakFileParser.ParseModules(stream))
                    {
                        if (!string.IsNullOrEmpty(module.Uuid)) modules[module.Uuid] = module;
                    }
                }
                catch (Exception e) when (e is InvalidDataException or XmlException or IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(e, "Unable to read the modules of game pak `{Pak}`", pak);
                }
            }

            _cache[dataFolder] = (fingerprint, modules);
            return modules.Count == 0 ? null : modules;
        }
    }

    /// <summary>
    /// A split pak continues in `Name_1.pak`, `Name_2.pak`, ... next to `Name.pak`. The parts hold only file data,
    /// no header, so opening one only fails and logs a warning.
    /// </summary>
    private static bool IsArchivePart(string fileName, IReadOnlySet<string> pakNames)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var underscore = stem.LastIndexOf('_');
        if (underscore <= 0 || underscore == stem.Length - 1) return false;
        if (stem.AsSpan(underscore + 1).ContainsAnyExceptInRange('0', '9')) return false;
        return pakNames.Contains(stem[..underscore] + Bg3Constants.PakFileExtension.ToString());
    }
}
