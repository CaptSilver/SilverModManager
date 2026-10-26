using Microsoft.Extensions.Logging;
using SilverModManager.Games.Generic.ModProvenance;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Cleanup.SaveToLibrary;

/// <summary>Works out what a loadout's cleanup would delete that Nexus Mods can give back.</summary>
public interface ILibrarySaver
{
    Task<SavePlan> PlanAsync(Loadout.ReadOnly loadout, CleanupPlan cleanup, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class LibrarySaver(INexusCatalog catalog, ILibraryProbe library, ILogger<LibrarySaver>? logger = null) : ILibrarySaver
{
    public async Task<SavePlan> PlanAsync(Loadout.ReadOnly loadout, CleanupPlan cleanup, CancellationToken cancellationToken = default)
    {
        var installation = loadout.InstallationInstance;
        var game = installation.Game as IFromSoftGame
                   ?? throw new InvalidOperationException($"{installation.Game.DisplayName} is not a FromSoft game, so its game folder holds no mods to save.");
        var gameFolder = FromSoftPaths.GameFolder(game.Layout, installation);
        var nexusId = game.NexusModsGameId.HasValue ? game.NexusModsGameId.Value : (Sdk.NexusModsApi.NexusModsGameId?)null;

        // The folder is read here, off the UI thread, before any network call.
        var inventory = await Task.Run(() => GameFolderInventory.Take(gameFolder), cancellationToken);
        var fullGameFolder = gameFolder.GetFullPath();

        // Other managers know the game by its name, and by the folder it is installed in ("ELDEN RING").
        var names = new[] { installation.Game.DisplayName, Path.GetFileName(Path.GetDirectoryName(fullGameFolder)) }.OfType<string>().Where(n => n.Length > 0).ToArray();
        var search = new ManagerSearch(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), [], []);
        var scope = new SaveScope(fullGameFolder, game.Cleanup.ModsFolder, inventory, cleanup, search, names);
        return await new SavePlanner(catalog, library, logger).PlanAsync(nexusId, scope, cancellationToken);
    }
}
