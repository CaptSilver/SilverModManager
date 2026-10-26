using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using SilverModManager.Games.Generic.ModProvenance;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Games.FromSoft.Cleanup.SaveToLibrary;

/// <param name="GameFolder">The absolute path of the <c>Game</c> folder.</param>
/// <param name="ModsFolder">The name of the folder other managers kept one subfolder per mod in, or null.</param>
/// <param name="Inventory">Everything in the game folder, see <see cref="GameFolderInventory.Take"/>.</param>
/// <param name="Plan">The cleanup the user is about to confirm.</param>
/// <param name="Search">Where other managers' records and archives are looked for; null looks nowhere.</param>
/// <param name="GameNames">What other managers call the game, to tell which of their instances are for it.</param>
public sealed record SaveScope(
    string GameFolder,
    string? ModsFolder,
    IReadOnlyList<InventoryEntry> Inventory,
    CleanupPlan Plan,
    ManagerSearch? Search = null,
    IReadOnlyList<string>? GameNames = null);

/// <summary>
/// Works out which of the mods a cleanup is about to delete can be fetched from Nexus Mods again. It goes by what other
/// managers and archives say, in this order: another manager's own records, the lock file, an archive whose hash Nexus
/// Mods knows, and only when there is no lock file, the folder's name checked against the files of the page it names.
/// Whatever it cannot tell is listed with the reason, never guessed.
/// </summary>
public sealed class SavePlanner(INexusCatalog catalog, ILibraryProbe library, ILogger? logger = null)
{
    private readonly NexusFileResolver _resolver = new(catalog, logger);
    private readonly ModIdentifier _identifier = new(logger);

    public async Task<SavePlan> PlanAsync(NexusModsGameId? game, SaveScope scope, CancellationToken cancellationToken)
    {
        var folders = DeletedModFolders(scope);
        if (folders.Count == 0) return new SavePlan(game, [], []);

        var (entries, problems) = LockFileLocator.Read(scope);
        var save = new List<ModToSave>();
        var listed = new List<ListedMod>();

        var identified = await IdentifyAsync(game, scope, folders, entries, cancellationToken);
        problems = problems.AddRange(identified.Problems);

        var planned = new HashSet<ProvenanceEntry>();
        foreach (var mod in identified.Mods)
        {
            if (mod.Chosen is not { } claim)
            {
                // A lock that is found means the folder names are not searched for: the lock is the authority on what it lists.
                if (entries.Length == 0) await PlanFolderAsync(game, scope, mod.Mod, save, listed, cancellationToken);
                continue;
            }

            foreach (var entry in entries.Where(e => e.Source == ProvenanceSources.Nexus && ModIdentifier.Matches(LockClaim(e), mod.Mod))) planned.Add(entry);
            await PlanClaimAsync(game, mod.Mod, claim, entries, save, listed, cancellationToken);
        }

        // Entries of the lock no deleted folder claimed are still offered: the lock says what the other manager installed.
        foreach (var entry in entries.Where(e => !planned.Contains(e)))
            await PlanEntryAsync(game, entry, LockClaim(entry).How, save, listed, cancellationToken);

        return new SavePlan(game, [..save.DistinctBy(m => m.FileId)], [..listed], problems);
    }

    private const string LockOrigin = "erm";

    private async Task<IdentifyResult> IdentifyAsync(NexusModsGameId? game, SaveScope scope, IReadOnlyList<string> folders, ImmutableArray<ProvenanceEntry> entries, CancellationToken cancellationToken)
    {
        var identity = new GameIdentity(scope.GameFolder, scope.GameNames ?? [], game?.Value);
        var search = scope.Search ?? ManagerSearch.None;

        // erm keeps downloaded release archives in a vendor folder beside its lock file.
        var vendor = LockFileLocator.Candidates(scope).Where(File.Exists).Select(l => Path.Combine(Path.GetDirectoryName(l) ?? scope.GameFolder, "vendor"));
        search = search with { ExtraArchiveFolders = [..search.ExtraArchiveFolders, ..vendor] };

        var request = new IdentifyRequest(identity, folders, search, [..entries.Where(e => e.Source == ProvenanceSources.Nexus).Select(LockClaim)]);
        try
        {
            return await _identifier.IdentifyAsync(request, catalog, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger?.LogWarning(e, "Identifying the old mods failed");
            return new IdentifyResult([..folders.Select(f => new ModIdentification(f, null, []))], [$"The old mods could not be checked against other managers' records: {e.Message}"]);
        }
    }

    private static ModClaim LockClaim(ProvenanceEntry entry)
        => new(entry.Id, ClaimMethod.LockFile, LockOrigin, ClaimSource.Nexus,
            entry.ModId ?? (entry.Asset is null ? null : ArchiveName.TryParse(entry.Asset, entry.Version)?.ModId),
            entry.FileId, entry.Version, entry.Asset);

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

    private async Task PlanClaimAsync(NexusModsGameId? game, string folder, ModClaim claim, ImmutableArray<ProvenanceEntry> entries, List<ModToSave> save, List<ListedMod> listed, CancellationToken cancellationToken)
    {
        if (claim.Source == ClaimSource.Thunderstore)
        {
            listed.Add(new ListedMod(folder, ListedReason.Thunderstore, $"{claim.Describe()} is a Thunderstore package ({claim.How}). Packages are not downloaded, so install it again from Thunderstore if you want it."));
            return;
        }

        // A lock entry keeps the id the lock gave it, so what the user sees matches the file they know. Its file id may
        // have come from an archive when the lock named none.
        var entry = claim.Method == ClaimMethod.LockFile
            ? entries.First(e => e.Source == ProvenanceSources.Nexus && e.Id == claim.Mod) is var locked && locked.FileId is null ? locked with { FileId = claim.FileId } : locked
            : new ProvenanceEntry(folder, ProvenanceSources.Nexus, claim.Version, claim.FileId, claim.Archive, claim.ModId);
        await PlanEntryAsync(game, entry, claim.How, save, listed, cancellationToken);
    }

    private async Task PlanEntryAsync(NexusModsGameId? game, ProvenanceEntry entry, string how, List<ModToSave> save, List<ListedMod> listed, CancellationToken cancellationToken)
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
        Add(nexusGame, resolved, entry.Id, how, save, listed);
    }

    private async Task PlanFolderAsync(NexusModsGameId? game, SaveScope scope, string folder, List<ModToSave> save, List<ListedMod> listed, CancellationToken cancellationToken)
    {
        if (game is not { } nexusGame)
        {
            listed.Add(new ListedMod(folder, ListedReason.Unidentified, "The game has no Nexus Mods id, so its files cannot be looked up."));
            return;
        }

        FileResolution resolved;
        try
        {
            resolved = await ResolveFolderAsync(nexusGame, scope, folder, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            resolved = new FileResolution(null, $"Nexus Mods could not be asked about it: {e.Message}");
        }

        Add(nexusGame, resolved, folder, "by name, files checked", save, listed);
    }

    private async Task<FileResolution> ResolveFolderAsync(NexusModsGameId game, SaveScope scope, string folder, CancellationToken cancellationToken)
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
        if (file is null) return new FileResolution(null, $"Nexus Mods lists no file {fileId} for mod {modId}.");

        // The name alone is not enough: a different mod can share it. The folder has to hold this download's files.
        var local = ContentCheck.LocalFiles(Path.Combine(scope.GameFolder, scope.ModsFolder ?? "", folder));
        var listing = await catalog.ListArchiveContentAsync(game.Value, modId, fileId, cancellationToken);
        if (listing is null) return new FileResolution(null, $"Nexus Mods has no file listing for file {fileId} of mod {modId}, so the name match could not be checked against the folder's files.");
        if (!ContentCheck.Accepts(local, listing))
            return new FileResolution(null, $"The files in the folder do not match the files of file {fileId} of mod {modId}, which only has the same name.");

        return new FileResolution(new ModToSave(folder, file.Name, file.Version, modId, fileId), null);
    }

    private void Add(NexusModsGameId game, FileResolution resolved, string id, string how, List<ModToSave> save, List<ListedMod> listed)
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
            save.Add(mod with { IdentifiedBy = how });
        }
    }
}
