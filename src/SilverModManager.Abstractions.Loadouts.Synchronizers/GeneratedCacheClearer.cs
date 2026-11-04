using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Sdk.GeneratedCaches;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// Deletes what the registered <see cref="IGeneratedCache"/>s say an Apply made stale. Runs after the Apply
/// committed, so a failed Apply leaves every cache alone, and never fails the Apply: an item that cannot be
/// deleted is logged and the rest go on.
/// </summary>
public sealed class GeneratedCacheClearer(IReadOnlyCollection<IGeneratedCache> caches, ILogger logger)
{
    /// <summary>Whether a cache owns the path, so it is the mod's data and not a file of the player's.</summary>
    public bool IsOwnedPath(GamePath path)
    {
        foreach (var cache in caches)
        {
            if (cache.IsOwnedPath(path)) return true;
        }

        return false;
    }

    /// <summary>
    /// Deletes the stale items of every cache that applies to the installation.
    /// </summary>
    /// <param name="installation">The installation the Apply wrote to.</param>
    /// <param name="changed">Every game path the Apply added, replaced or removed.</param>
    /// <returns>The number of files and folders deleted.</returns>
    public int Clear(GameInstallation installation, IReadOnlyCollection<GamePath> changed)
    {
        if (caches.Count == 0 || changed.Count == 0) return 0;

        var cleared = 0;
        foreach (var cache in caches)
        {
            try
            {
                if (!cache.AppliesTo(installation, changed)) continue;
                foreach (var item in cache.StaleItems(installation, changed)) cleared += Delete(cache, item);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Whatever an ini value or the disk throws: the Apply is committed, so there is nothing to roll back.
                logger.LogWarning(e, "Could not check the {Cache} cache after the Apply; it is left as it is", cache.Name);
            }
        }

        if (cleared > 0) logger.LogInformation("Cleared {Count} stale cache item(s) after the Apply", cleared);
        return cleared;
    }

    private int Delete(IGeneratedCache cache, AbsolutePath item)
    {
        try
        {
            if (item.DirectoryExists()) item.DeleteDirectory(recursive: true);
            else if (item.FileExists) item.Delete();
            else return 0;

            logger.LogInformation("Cleared {Item}: the {Cache} cache is stale after the Apply", item, cache.Name);
            return 1;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Could not clear {Item} from the {Cache} cache; it is left in place", item, cache.Name);
            return 0;
        }
    }
}
