using System.Reactive;
using ReactiveUI;
using SilverModManager.App.UI.Overlays;
using SilverModManager.Platform.Proton.Steam.Shortcuts;

namespace SilverModManager.App.UI.Pages.SteamShortcut;

/// <summary>
/// The "Show in Steam" dialog: adds the app to Steam's library, or takes it out again, by whichever route the state
/// of Steam allows.
/// </summary>
public interface ISteamShortcutViewModel : IOverlayViewModel<Unit>
{
    string Title { get; }

    /// <summary>
    /// True when the app is in Steam's library (or was asked to be), so the buttons remove it.
    /// </summary>
    bool IsRemoving { get; }

    bool IsSteamRunning { get; }

    /// <summary>
    /// The accounts to choose between when more than one could own the shortcut; empty when the account is known.
    /// </summary>
    IReadOnlyList<SteamAccount> Accounts { get; }

    SteamAccount? SelectedAccount { get; set; }

    /// <summary>Add with Steam closed.</summary>
    bool CanAdd { get; }

    /// <summary>Close Steam, add, start Steam again.</summary>
    bool CanCloseAndAdd { get; }

    /// <summary>Ask the running Steam to add it.</summary>
    bool CanAskSteam { get; }

    /// <summary>Remove with Steam closed.</summary>
    bool CanRemove { get; }

    /// <summary>Close Steam, remove, start Steam again.</summary>
    bool CanCloseAndRemove { get; }

    bool IsWaitingForSteam { get; }

    /// <summary>What the running Steam route leaves undone, shown beside its button.</summary>
    string AskSteamNote { get; }

    /// <summary>Why a button is missing (Steam is running, or this is Game Mode), or empty.</summary>
    string Note { get; }

    /// <summary>The outcome of the last button, or empty.</summary>
    string Message { get; }

    ReactiveCommand<Unit, Unit> AddCommand { get; }
    ReactiveCommand<Unit, Unit> CloseAndAddCommand { get; }
    ReactiveCommand<Unit, Unit> AskSteamCommand { get; }
    ReactiveCommand<Unit, Unit> RemoveCommand { get; }
    ReactiveCommand<Unit, Unit> CloseAndRemoveCommand { get; }
    ReactiveCommand<Unit, Unit> CancelWaitCommand { get; }
    ReactiveCommand<Unit, Unit> CloseCommand { get; }
}
