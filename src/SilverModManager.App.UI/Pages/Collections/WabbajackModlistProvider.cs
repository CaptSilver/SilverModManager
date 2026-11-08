using System.Reactive.Linq;
using DynamicData;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Collections;
using SilverModManager.App.UI.Pages.Collections.Update;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Pages.LibraryPage.Collections;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack;
using SilverModManager.Wabbajack.Models;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.App.UI.Pages.Collections;

/// <summary>
/// Wabbajack modlists: a group is a <see cref="WabbajackModlistLoadoutGroup"/>, and every modlist in the library that
/// is for the loadout's game and not installed there is pending. A list is listed as installed once its install ran to
/// its end without skipping files, so it is on exactly one side of that line in each loadout.
/// </summary>
public sealed class WabbajackModlistProvider(IServiceProvider services) : ICollectionProvider
{
    public bool Owns(CollectionGroup.ReadOnly group) => group.TryGetAsWabbajackModlistLoadoutGroup(out _);

    public CollectionSource ForGroup(CollectionGroup.ReadOnly group, LoadoutId loadout) =>
        new WabbajackModlistSource(services, loadout, ModlistGroup(group).Modlist);

    public IObservable<bool> IsListedAsInstalled(CollectionGroup.ReadOnly group)
    {
        var modlist = ModlistGroup(group);
        var loadout = group.AsLoadoutItemGroup().AsLoadoutItem().LoadoutId;
        return WabbajackModlistInstallState.Observe(services.GetRequiredService<IConnection>(), LibraryFileId.From(modlist.ModlistId.Value), loadout)
            .Select(static install => install.State == WabbajackModlistCardState.Installed)
            .DistinctUntilChanged();
    }

    public IObservable<ICollectionUpdate?> ObserveUpdate(CollectionGroup.ReadOnly group) => Observable.Return<ICollectionUpdate?>(null);

    public IObservable<IChangeSet<CollectionSource, EntityId>> ObservePending(Loadout.ReadOnly loadout)
    {
        var connection = services.GetRequiredService<IConnection>();
        var loadoutId = loadout.LoadoutId;
        // The exact title: a Skyrim VR list is not offered to a Skyrim SE loadout, whatever domain Nexus files them under.
        var games = new LoadoutAcquisition(loadoutId).Destination(connection.Db).Games;

        return WabbajackModlistLibraryFile
            .ObserveAll(connection)
            .FilterImmutable(file => WabbajackGames.GameIdFor(file.GameType) is { } game && games.Contains(game))
            .FilterOnObservable(file => WabbajackModlistInstallState.Observe(connection, LibraryFileId.From(file.Id), loadoutId)
                .Select(static install => install.State != WabbajackModlistCardState.Installed)
                .DistinctUntilChanged())
            .Transform(CollectionSource (file) => new WabbajackModlistSource(services, loadoutId, file));
    }

    private static WabbajackModlistLoadoutGroup.ReadOnly ModlistGroup(CollectionGroup.ReadOnly group) =>
        group.TryGetAsWabbajackModlistLoadoutGroup(out var modlist)
            ? modlist
            : throw new InvalidOperationException($"Collection group {group.Id} is not a Wabbajack modlist");
}
