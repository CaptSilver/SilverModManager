using System.Reactive.Linq;
using DynamicData.Kernel;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;

// Not SilverModManager.Library.ModSources: App.UI has a static class ModSources and the names would clash.
namespace SilverModManager.Library;

/// <summary>
/// Items downloaded from Nexus Mods.
/// </summary>
internal sealed class NexusModSource(IModUpdateService modUpdateService) : IModSource
{
    public string DisplayName => "Nexus Mods";

    public bool Owns(LibraryItem.ReadOnly item) => NexusModsLibraryItem.TryGet(item.Db, item.Id, out _);

    public Uri? PageUri(LibraryItem.ReadOnly item)
    {
        if (NexusModsLibraryItem.TryGet(item.Db, item.Id, out var nexusItem) is false) return null;
        var modPage = nexusItem.Value.ModPageMetadata;
        return NexusModsUrlBuilder.GetModUri(modPage.GameDomain, modPage.Uid.ModId);
    }

    // The site has no changelog page for a single file (the popup that shows one can't be linked to), only the
    // mod page's own changelog tab, so both actions open the mod page.
    public Uri? ChangelogUri(LibraryItem.ReadOnly item) => PageUri(item);

    // The Nexus check is global, not per game, and throttles itself, so neither game nor force changes what runs.
    public async Task CheckUpdates(IGameData game, bool force, CancellationToken cancellationToken)
        => await modUpdateService.CheckAndUpdateModPages(cancellationToken, notify: true);

    public IObservable<Optional<ModUpdate>> ObserveUpdate(LibraryItem.ReadOnly item)
    {
        if (NexusModsLibraryItem.TryGet(item.Db, item.Id, out var nexusItem) is false) return Observable.Return(Optional<ModUpdate>.None);

        return modUpdateService
            .GetNewestFileVersionObservable(nexusItem.Value.FileMetadata)
            .Select(static update => AsUpdate(update.HasValue ? (ModUpdatesOnModPage)update.Value : null));
    }

    public IObservable<Optional<ModUpdate>> ObserveUpdate(IReadOnlyList<LibraryItem.ReadOnly> items)
    {
        // The items of a parent row all belong to its mod page, so any one of them names it.
        foreach (var item in items)
        {
            if (NexusModsLibraryItem.TryGet(item.Db, item.Id, out var nexusItem) is false) continue;

            return modUpdateService
                .GetNewestModPageVersionObservable(nexusItem.Value.ModPageMetadata)
                .Select(static updates => AsUpdate(updates.HasValue ? updates.Value : null));
        }
        return Observable.Return(Optional<ModUpdate>.None);
    }

    // The update service has already left out the files whose updates are hidden.
    public IReadOnlyList<ModUpdate> AllUpdates(IGameData game)
    {
        var updates = new List<ModUpdate>();
        foreach (var (_, onPage) in modUpdateService.GetAllModPagesWithUpdates(game.GameId))
        {
            if (NexusModUpdate.From(onPage) is { } update) updates.Add(update);
        }
        return updates;
    }

    // The Nexus check is global, not per game.
    public IObservable<bool> ObserveHasAnyUpdates(IGameData game) => modUpdateService.HasAnyUpdatesObservable();

    private static Optional<ModUpdate> AsUpdate(ModUpdatesOnModPage? updates)
        => updates is { } found && NexusModUpdate.From(found) is { } update ? Optional<ModUpdate>.Create(update) : Optional<ModUpdate>.None;
}
