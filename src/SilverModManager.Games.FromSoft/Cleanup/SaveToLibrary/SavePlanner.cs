using System.Collections.Immutable;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Games.FromSoft.Cleanup.SaveToLibrary;

/// <param name="GameFolder">The absolute path of the <c>Game</c> folder.</param>
/// <param name="ModsFolder">The name of the folder other managers kept one subfolder per mod in, or null.</param>
/// <param name="Inventory">Everything in the game folder, see <see cref="GameFolderInventory.Take"/>.</param>
/// <param name="Plan">The cleanup the user is about to confirm.</param>
public sealed record SaveScope(string GameFolder, string? ModsFolder, IReadOnlyList<InventoryEntry> Inventory, CleanupPlan Plan);

/// <summary>
/// Works out which of the mods a cleanup is about to delete can be fetched from Nexus Mods again. It trusts a lock file
/// another manager left; only when there is none does it look at the folder names, and then only for an exact,
/// single match. Whatever it cannot tell is listed with the reason, never guessed.
/// </summary>
public sealed class SavePlanner(INexusCatalog catalog, ILibraryProbe library)
{
    private readonly NexusFileResolver _resolver = new(catalog);

    public async Task<SavePlan> PlanAsync(NexusModsGameId? game, SaveScope scope, CancellationToken cancellationToken)
    {
        var folders = DeletedModFolders(scope);
        if (folders.Count == 0) return new SavePlan(game, [], []);

        var (entries, problems) = LockFileLocator.Read(scope);
        var save = new List<ModToSave>();
        var listed = new List<ListedMod>();

        if (entries.Length > 0)
        {
            foreach (var entry in entries) await PlanEntryAsync(game, entry, save, listed, cancellationToken);
        }
        else
        {
            foreach (var folder in folders) await PlanFolderAsync(game, folder, save, listed, cancellationToken);
        }

        return new SavePlan(game, [..save.DistinctBy(m => m.FileId)], [..listed], problems);
    }

    /// <summary>The names of the mod folders a Delete step removes, inside the game's mods folder.</summary>
    private static IReadOnlyList<string> DeletedModFolders(SaveScope scope)
    {
        if (scope.ModsFolder is not { } mods) return [];

        return scope.Plan.Steps
            .Where(s => s.Kind == CleanupStepKind.Delete)
            .Select(s => s.Source?.Split('/') ?? [])
            .Where(parts => parts.Length == 2 && parts[0].Equals(mods, StringComparison.OrdinalIgnoreCase))
            .Select(parts => parts[1])
            .ToArray();
    }

    private async Task PlanEntryAsync(NexusModsGameId? game, ProvenanceEntry entry, List<ModToSave> save, List<ListedMod> listed, CancellationToken cancellationToken)
    {
        if (entry.Source == ProvenanceSources.GitHub)
        {
            listed.Add(new ListedMod(entry.Id, ListedReason.GitHub, $"{entry.Asset ?? entry.Id} is a GitHub release. Releases are not downloaded, so keep a copy yourself if you want it."));
            return;
        }

        if (entry.Source != ProvenanceSources.Nexus)
        {
            listed.Add(new ListedMod(entry.Id, ListedReason.Unidentified, $"Its source '{entry.Source}' is not Nexus Mods or GitHub."));
            return;
        }

        if (game is not { } nexusGame)
        {
            listed.Add(new ListedMod(entry.Id, ListedReason.Unidentified, "The game has no Nexus Mods id, so its files cannot be looked up."));
            return;
        }

        var resolved = await _resolver.ResolveAsync(nexusGame, entry, cancellationToken);
        Add(nexusGame, resolved, entry.Id, save, listed);
    }

    private async Task PlanFolderAsync(NexusModsGameId? game, string folder, List<ModToSave> save, List<ListedMod> listed, CancellationToken cancellationToken)
    {
        if (game is not { } nexusGame)
        {
            listed.Add(new ListedMod(folder, ListedReason.Unidentified, "The game has no Nexus Mods id, so its files cannot be looked up."));
            return;
        }

        FileResolution resolved;
        try
        {
            resolved = await ResolveFolderAsync(nexusGame, folder, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            resolved = new FileResolution(null, $"Nexus Mods could not be asked about it: {e.Message}");
        }

        Add(nexusGame, resolved, folder, save, listed);
    }

    private async Task<FileResolution> ResolveFolderAsync(NexusModsGameId game, string folder, CancellationToken cancellationToken)
    {
        var wanted = NexusFileResolver.Squash(folder);
        var hits = (await catalog.FindModsByNameAsync(game, folder, cancellationToken))
            .Where(h => NexusFileResolver.Squash(h.Name) == wanted)
            .ToArray();
        if (hits.Length != 1) return new FileResolution(null, hits.Length == 0 ? "No mod of the game has exactly this name on Nexus Mods." : "More than one mod of the game has exactly this name on Nexus Mods.");

        var modId = hits[0].ModId;
        if (await catalog.FindMainFileAsync(game, modId, cancellationToken) is not { } fileId)
            return new FileResolution(null, $"Mod {modId} does not have exactly one main file, so the right download is unclear.");

        var file = (await catalog.ListFilesAsync(game, modId, cancellationToken))?.FirstOrDefault(f => f.FileId == fileId);
        return file is null
            ? new FileResolution(null, $"Nexus Mods lists no file {fileId} for mod {modId}.")
            : new FileResolution(new ModToSave(folder, file.Name, file.Version, modId, fileId), null);
    }

    private void Add(NexusModsGameId game, FileResolution resolved, string id, List<ModToSave> save, List<ListedMod> listed)
    {
        if (resolved.Mod is not { } mod)
        {
            listed.Add(new ListedMod(id, ListedReason.Unidentified, $"Could not tell which Nexus Mods file it is. {resolved.Why}"));
        }
        else if (library.Contains(game, mod.FileId))
        {
            listed.Add(new ListedMod(id, ListedReason.AlreadyInLibrary, $"{mod.Name} {mod.Version} is already in your library."));
        }
        else
        {
            save.Add(mod);
        }
    }
}
