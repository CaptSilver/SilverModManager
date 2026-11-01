using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Networking.NexusWebApi;

/// <summary>
/// The last evidence: Nexus Mods names a game id, and sibling games share one (Skyrim SE and Skyrim VR), so it cannot
/// say which. The item is for every game of that id the user manages, marked shared when that is more than one, or for
/// every game of the id this build knows when none is managed.
/// </summary>
/// <remarks>
/// "Managed" is read from the database (the game of every installation a loadout points to), not from the game
/// registry: a migration runs at startup, before anything should scan game stores, and the database answer is the same
/// in a test.
/// </remarks>
public sealed class NexusDomainInference(IEnumerable<IGameData> games) : IDestinationInference
{
    /// <inheritdoc />
    public int Priority => 40;

    /// <inheritdoc />
    public Destination Infer(IDb db, LibraryItem.ReadOnly item)
    {
        if (NexusIdOf(db, item) is not { } nexusId) return Destination.Unknown;

        var sameId = games.Where(game => game.NexusModsGameId.HasValue && game.NexusModsGameId.Value == nexusId).ToArray();
        var managed = ManagedGames(db);
        var chosen = sameId.Where(game => managed.Contains(game.GameId)).ToArray();
        if (chosen.Length == 0) chosen = sameId;

        return chosen.Length == 0
            ? Destination.Unknown
            : new Destination(chosen.Select(game => game.GameId).ToHashSet(), Shared: chosen.Length > 1);
    }

    private static NexusModsGameId? NexusIdOf(IDb db, LibraryItem.ReadOnly item)
    {
        var modFile = NexusModsLibraryItem.Load(db, item.Id);
        if (modFile.IsValid()) return modFile.ModPageMetadata.Uid.GameId;

        var collectionFile = NexusModsCollectionLibraryFile.Load(db, item.Id);
        if (!collectionFile.IsValid()) return null;

        foreach (var revision in CollectionRevisionMetadata.FindByRevisionNumber(db, collectionFile.CollectionRevisionNumber))
        {
            if (revision.Collection.Slug == collectionFile.CollectionSlug) return revision.Collection.GameId;
        }

        return null;
    }

    private static HashSet<GameId> ManagedGames(IDb db)
    {
        var managed = new HashSet<GameId>();
        foreach (var loadout in Loadout.All(db))
        {
            if (GameInstallMetadata.Game.TryGetValue(loadout.Installation, out var game)) managed.Add(game);
        }

        return managed;
    }
}
