using System.Reactive.Linq;
using DynamicData.Kernel;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Library;
using SilverModManager.Library.Workshop;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;

// Not SilverModManager.Library.ModSources: App.UI has a static class ModSources and the names would clash.
namespace SilverModManager.Library;

/// <summary>
/// Steam Workshop items as an <see cref="IModSource"/>: it owns nothing in the library, because Steam keeps the files in
/// its own folder and the app never writes there. Checking for updates asks Steam about the game's items again, so the
/// Workshop rows say which ones Steam has a newer copy of.
/// </summary>
/// <remarks>
/// It never reports an update to Update All. Steam downloads a Workshop update by itself, so there is nothing for the
/// app to apply, and a lit Update All button would do nothing when clicked.
/// </remarks>
internal sealed class SteamWorkshopSource(IGameRegistry registry, WorkshopItemsView view, ILogger<SteamWorkshopSource> logger) : IModSource
{
    public string DisplayName => "Steam Workshop";

    public bool Owns(LibraryItem.ReadOnly item) => false;

    public bool Owns(ModUpdate update) => false;

    public Uri? PageUri(LibraryItem.ReadOnly item) => null;

    public Uri? ChangelogUri(LibraryItem.ReadOnly item) => null;

    public async Task CheckUpdates(IGameData game, bool force, CancellationToken cancellationToken)
    {
        if (SteamWorkshopGame.For(game) is null) return;

        foreach (var install in registry.LocateGameInstallations().Where(install => install.Game.GameId == game.GameId))
        {
            try
            {
                await view.For(install, cancellationToken, force);
            }
            catch (Exception e) when (e is UserFacingException or IOException)
            {
                logger.LogWarning("Not checking the Steam Workshop items of {Game} for updates: {Reason}", game.DisplayName, e.Message);
            }
        }
    }

    // The rows have no library item to attach to, so no row ever shows an update.
    public IObservable<Optional<ModUpdate>> ObserveUpdate(LibraryItem.ReadOnly item) => Observable.Return(Optional<ModUpdate>.None);

    public IObservable<Optional<ModUpdate>> ObserveUpdate(IReadOnlyList<LibraryItem.ReadOnly> items) => Observable.Return(Optional<ModUpdate>.None);

    public IReadOnlyList<ModUpdate> AllUpdates(IGameData game) => [];

    public IObservable<bool> ObserveHasAnyUpdates(IGameData game) => Observable.Return(false);

    public bool WillReplace(ModUpdateRequest request) => false;

    // Steam already applied it, or applies it next time it runs.
    public ValueTask<ModUpdateOutcome> ApplyUpdates(ModUpdateRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(ModUpdateOutcome.Nothing);

    public ValueTask SetUpdatesHidden(IReadOnlyList<LibraryItem.ReadOnly> items, bool hidden, CancellationToken cancellationToken)
        => items.Count == 0
            ? ValueTask.CompletedTask
            : throw new ArgumentException("Steam Workshop owns no library items, so there are no updates to hide.", nameof(items));

    public IObservable<HideUpdatesState> ObserveHideUpdates(IReadOnlyList<LibraryItem.ReadOnly> items)
        => Observable.Return(new HideUpdatesState(IsHidden: false, ItemCount: 0, IsEnabled: false, IsVisible: false));

    public HideUpdatesLabelKind HideUpdatesLabel(bool hidden) => hidden ? HideUpdatesLabelKind.Show : HideUpdatesLabelKind.Hide;
}
