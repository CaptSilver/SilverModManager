using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Cleanup.SaveToLibrary;

/// <summary>Works out what a loadout's cleanup would delete that Nexus Mods can give back.</summary>
public interface ILibrarySaver
{
    Task<SavePlan> PlanAsync(Loadout.ReadOnly loadout, CleanupPlan cleanup, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class LibrarySaver(INexusCatalog catalog, ILibraryProbe library) : ILibrarySaver
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
        var scope = new SaveScope(gameFolder.GetFullPath(), game.Cleanup.ModsFolder, inventory, cleanup);
        return await new SavePlanner(catalog, library).PlanAsync(nexusId, scope, cancellationToken);
    }
}
