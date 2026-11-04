using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.GeneratedCaches;

/// <summary>
/// The statics SkyrimPrecombines (Faster Objects) bakes at run time, kept per game setup in a folder under the
/// prefix's local app data. It bakes from plugins, meshes and archives, so a change to any of them invalidates all
/// of it; a change to anything else, a texture or an ini, does not.
/// </summary>
public sealed class SkyrimPrecombinesCache(IReadOnlyCollection<GameId> games, ILogger<SkyrimPrecombinesCache> logger) : ASksePluginCache(games, logger)
{
    private static readonly GamePath Meshes = DataFolder / "Meshes";

    /// <inheritdoc />
    public override string Name => "SkyrimPrecombines";

    /// <inheritdoc />
    protected override string PluginName => "SkyrimPrecombines";

    /// <inheritdoc />
    protected override string FolderKey => "sCacheFolder";

    /// <inheritdoc />
    /// <remarks>Next to the game's own app data folder, which is <c>AppData/Local/&lt;game&gt;</c> inside the prefix.</remarks>
    protected override AbsolutePath? DefaultFolder(GameInstallation installation)
        => installation.Locations.TryGetValue(LocationId.AppData, out var appData) ? appData.Path.Parent / "FasterObjects" : null;

    /// <inheritdoc />
    /// <remarks>The default folder is outside every game location, so there is nothing in the game folder to claim.</remarks>
    public override bool IsOwnedPath(GamePath path) => false;

    /// <inheritdoc />
    public override IReadOnlyList<AbsolutePath> StaleItems(GameInstallation installation, IReadOnlyCollection<GamePath> changed)
    {
        if (!changed.Any(IsBakedFrom)) return [];

        var stale = new List<AbsolutePath>();
        foreach (var folder in OwnedFolders(installation))
        {
            if (!folder.DirectoryExists()) continue;
            if (!IsSafeToEmpty(installation, folder))
            {
                Logger.LogWarning("Not clearing {Folder}: it is, holds or sits inside a game folder", folder);
                continue;
            }

            // Only what the plugin made: a folder per game setup holding meshes or a runtime bake. Anything else in a
            // folder the ini names is not the plugin's.
            stale.AddRange(folder.EnumerateDirectories(recursive: false).Where(IsBakeFolder));
        }

        return stale;
    }

    private static bool IsBakeFolder(AbsolutePath folder)
        => folder.EnumerateDirectories(recursive: false).Any(child => child.FileName.Equals("meshes", StringComparison.OrdinalIgnoreCase)
            || child.FileName.Equals("RuntimeBake", StringComparison.OrdinalIgnoreCase));

    private static bool IsBakedFrom(GamePath path)
    {
        if (path.LocationId != LocationId.Game) return false;
        if (path.InFolder(Meshes)) return true;

        var extension = path.Extension;
        return path.Parent == DataFolder && (extension == KnownCEExtensions.ESP || extension == KnownCEExtensions.ESM
            || extension == KnownCEExtensions.ESL || extension == KnownCEExtensions.BSA || extension == KnownCEExtensions.BA2);
    }
}
