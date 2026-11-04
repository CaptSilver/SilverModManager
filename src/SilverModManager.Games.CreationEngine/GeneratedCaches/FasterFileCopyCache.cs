using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.GeneratedCaches;

/// <summary>
/// FasterFileCopy's decompression cache: one <c>&lt;Archive&gt;.bsa.decomp</c> per archive and a
/// <c>.decomp.lock</c> beside it. Nothing in its ini ties an entry to the archive it came from, so an archive that
/// changed or went away takes its entry with it and every other entry stays.
/// </summary>
public sealed class FasterFileCopyCache(IReadOnlyCollection<GameId> games, ILogger<FasterFileCopyCache> logger) : ASksePluginCache(games, logger)
{
    private static readonly GamePath DefaultCache = PluginsFolder / "FasterFileCopy_cache";
    private static readonly string[] EntrySuffixes = [".bsa.decomp", ".bsa.decomp.lock"];

    /// <inheritdoc />
    public override string Name => "FasterFileCopy";

    /// <inheritdoc />
    protected override string PluginName => "FasterFileCopy";

    /// <inheritdoc />
    protected override string FolderKey => "sCacheDir";

    /// <inheritdoc />
    protected override AbsolutePath? DefaultFolder(GameInstallation installation) => installation.Locations.ToAbsolutePath(DefaultCache);

    /// <inheritdoc />
    public override bool IsOwnedPath(GamePath path) => path.InFolder(DefaultCache);

    /// <inheritdoc />
    public override IReadOnlyList<AbsolutePath> StaleItems(GameInstallation installation, IReadOnlyCollection<GamePath> changed)
    {
        var archives = changed
            .Where(path => path.LocationId == LocationId.Game && path.Extension == KnownCEExtensions.BSA)
            .Select(path => path.FileName.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (archives.Count == 0) return [];

        var stale = new List<AbsolutePath>();
        foreach (var folder in OwnedFolders(installation))
        {
            if (!folder.DirectoryExists()) continue;

            // Matched by name ignoring case: the folder keeps the spelling the game asked for, the loadout the mod's.
            foreach (var file in folder.EnumerateFiles(recursive: false))
            {
                var name = file.FileName;
                foreach (var suffix in EntrySuffixes)
                {
                    if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                    var archive = name[..^suffix.Length] + ".bsa";
                    if (archives.Contains(archive)) stale.Add(file);
                    break;
                }
            }
        }

        return stale;
    }
}
