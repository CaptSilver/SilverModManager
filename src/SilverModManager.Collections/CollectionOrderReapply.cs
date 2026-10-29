using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Collections;

/// <summary>The collection's order was not applied again; the message says why and nothing was changed.</summary>
public sealed class CollectionOrderReapplyRefusedException(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "The load order wasn't applied again";
}

/// <summary>What <see cref="CollectionOrderReapply.Reapply"/> did.</summary>
/// <param name="CollectionName">The collection's name as the loadout shows it.</param>
/// <param name="Dropped">How many entries of the order still match no mod after the import.</param>
public sealed record CollectionOrderReapplyResult(string CollectionName, int Dropped);

/// <summary>
/// Imports a collection's order into the loadout again with the current matching code. An install made with an older
/// matcher keeps whatever order that matcher produced and the entries it dropped; nothing else re-reads the manifest.
/// </summary>
public static class CollectionOrderReapply
{
    /// <summary>
    /// Reads the order out of the collection's manifest and applies it to every mod of the collection, replacing the
    /// order the mods are in and the stored dropped entries of the collection. Mods of other collections and the user's own keep their place.
    /// </summary>
    /// <exception cref="CollectionOrderReapplyRefusedException">The collection is being installed or updated, or its game is running.</exception>
    public static async Task<CollectionOrderReapplyResult> Reapply(IServiceProvider serviceProvider, NexusCollectionLoadoutGroup.ReadOnly group, CancellationToken cancellationToken = default)
    {
        var connection = serviceProvider.GetRequiredService<IConnection>();
        var loadoutId = group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().LoadoutId;
        var container = group.AsCollectionGroup().AsLoadoutItemGroup().LoadoutItemGroupId;
        var name = group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().Name;

        await CollectionUpdater.RunExclusive(serviceProvider, group, async () =>
        {
            var loadout = Loadout.Load(connection.Db, loadoutId);
            var installation = loadout.InstallationInstance;
            if (serviceProvider.GetRequiredService<IGameLaunchSession>().IsRunning(installation))
                throw new CollectionOrderReapplyRefusedException($"{installation.Game.DisplayName} is running from SilverModManager. Quit the game, then apply the load order again.");

            var current = NexusCollectionLoadoutGroup.Load(connection.Db, group.Id);
            var root = await serviceProvider.GetRequiredService<NexusModsLibrary>().ParseCollectionJsonFile(current.LibraryFile, cancellationToken);
            var game = installation.GetGame();

            // The sort orders learn about the installed mods from a background subscription; the order can only seed rows that exist.
            await game.SortOrderManager.UpdateLoadOrders(loadoutId, token: cancellationToken);

            using var tx = connection.BeginTransaction();
            CollectionOrderImport.Import(serviceProvider, connection.Db, loadoutId, game, root, current.Revision, container, newGroups: null, tx);
            await tx.Commit();
        }, cancellationToken);

        var dropped = ModOrderDroppedEntry.FindByLoadout(connection.Db, loadoutId).Count(row => row.ContainerId == container);
        return new CollectionOrderReapplyResult(name, dropped);
    }
}
