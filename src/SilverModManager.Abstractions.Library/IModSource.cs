using DynamicData.Kernel;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Abstractions.Library;

/// <summary>
/// One place a library item can come from. <see cref="ModSourceResolver"/> picks it per item. An item no source
/// owns (a local file, a Wabbajack archive, a bundled file) has none, and the UI shows that as disabled menu
/// items. No source invents a page for an item it doesn't know.
/// </summary>
public interface IModSource
{
    /// <summary>
    /// Matches the names in the Source column ("Nexus Mods", "Thunderstore").
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Whether the item was downloaded from this source.
    /// </summary>
    bool Owns(LibraryItem.ReadOnly item);

    /// <summary>
    /// Whether the update is one this source made, by the type it defines.
    /// </summary>
    bool Owns(ModUpdate update);

    /// <summary>
    /// The item's page on the source's site, or null when the source doesn't know the item's page.
    /// </summary>
    Uri? PageUri(LibraryItem.ReadOnly item);

    /// <summary>
    /// Where the source publishes changes for the item. Nexus has no per-file changelog page, so it returns the
    /// mod page.
    /// </summary>
    Uri? ChangelogUri(LibraryItem.ReadOnly item);

    /// <summary>
    /// Brings what the source knows about available updates up to date for one game and tells whoever observes
    /// them. Each source decides whether its own cache is fresh enough: Nexus throttles inside its update
    /// service, Thunderstore asks whether its catalog is stale. <paramref name="force"/> is the Refresh button
    /// and skips that test; entering a page passes false.
    /// </summary>
    Task CheckUpdates(IGameData game, bool force, CancellationToken cancellationToken);

    /// <summary>
    /// The update for one item, re-emitted after every <see cref="CheckUpdates"/>. None when the item is up to
    /// date or its update is hidden.
    /// </summary>
    IObservable<Optional<ModUpdate>> ObserveUpdate(LibraryItem.ReadOnly item);

    /// <summary>
    /// The update for a parent row: the newest across <paramref name="items"/>, which belong to one page or
    /// package, with <see cref="ModUpdate.CurrentItems"/> the items that have one.
    /// </summary>
    IObservable<Optional<ModUpdate>> ObserveUpdate(IReadOnlyList<LibraryItem.ReadOnly> items);

    /// <summary>
    /// Every update for the items shown for <paramref name="game"/>, which is what Update All applies.
    /// </summary>
    IReadOnlyList<ModUpdate> AllUpdates(IGameData game);

    /// <summary>
    /// Whether <paramref name="game"/> has any update to apply. Emits a value on subscribing, so a combination
    /// of sources never waits on one that has nothing to say.
    /// </summary>
    IObservable<bool> ObserveHasAnyUpdates(IGameData game);

    /// <summary>
    /// Whether <see cref="ApplyUpdates"/> would swap the new files in for the current ones in loadouts for this
    /// request. A source that can only hand the user a download link (a free Nexus Mods account) swaps nothing, so
    /// the caller has no reason to warn about the collections the swap would change.
    /// </summary>
    bool WillReplace(ModUpdateRequest request);

    /// <summary>
    /// Downloads the newest files of a batch of this source's updates and, when
    /// <see cref="ModUpdateRequest.Replace"/> is set, swaps them in for the current files in every loadout. One
    /// batch per source so a source can share work across updates. Shows no dialogs and removes no old library
    /// items; both belong to the caller. A failed download comes back in the outcome rather than thrown.
    /// </summary>
    ValueTask<ModUpdateOutcome> ApplyUpdates(ModUpdateRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Hides the updates for these items, or shows them again. Nexus hides the file uids newer than each item's
    /// file in its update filter. Thunderstore freezes the items, which the update check and the lock both honour.
    /// </summary>
    ValueTask SetUpdatesHidden(IReadOnlyList<LibraryItem.ReadOnly> items, bool hidden, CancellationToken cancellationToken);

    /// <summary>
    /// What the hide action needs to show for one item (a one-element list) or a parent row's items, emitted on
    /// subscribing and after every change to it.
    /// </summary>
    IObservable<HideUpdatesState> ObserveHideUpdates(IReadOnlyList<LibraryItem.ReadOnly> items);

    /// <summary>
    /// The menu text of the hide action without its count: "Hide updates" or "Show updates" for Nexus, "Freeze
    /// version" or "Unfreeze version" for Thunderstore, which holds the version back rather than only hiding it.
    /// </summary>
    string HideUpdatesLabel(bool hidden);
}
