using System.Reactive;
using SilverModManager.App.UI.Overlays;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.SteamLaunchOption;

/// <summary>
/// The 'Use this loadout from Steam' dialog: shows the launch options line for a loadout, copies it, or writes it
/// into Steam's config while Steam is closed.
/// </summary>
public interface ISteamLaunchOptionViewModel : IOverlayViewModel<Unit>
{
    string LoadoutName { get; }

    /// <summary>
    /// The launch options line.
    /// </summary>
    string Line { get; }

    /// <summary>
    /// Why the line may not work yet (the launcher script is missing), or empty.
    /// </summary>
    string Warning { get; }

    /// <summary>
    /// What Copy does to the system: nothing but fill the clipboard.
    /// </summary>
    string CopyNote { get; }

    bool IsSteamRunning { get; }

    /// <summary>
    /// Whether Write can be pressed, which is whenever Steam is closed.
    /// </summary>
    bool CanWrite { get; }

    /// <summary>
    /// Whether Close Steam and write can be pressed: Steam runs and no write is under way.
    /// </summary>
    bool CanCloseAndWrite { get; }

    /// <summary>
    /// True from the moment Steam has been asked to exit until it has, or the wait ends.
    /// </summary>
    bool IsWaitingForSteam { get; }

    /// <summary>
    /// Asks the user to close Steam while it runs, empty otherwise.
    /// </summary>
    string WriteNote { get; }

    /// <summary>
    /// The outcome of the last Copy or Write, empty before either was pressed.
    /// </summary>
    string Message { get; }

    /// <summary>
    /// Who owns the launch line Steam holds now: a name such as "Fluorine", or "another launcher" when the manager is
    /// not recognised. Empty while no replacement is pending.
    /// </summary>
    string OtherManagerName { get; }

    /// <summary>
    /// The other manager's line as it is in Steam's config, the one Replace will swap out. Empty while no replacement
    /// is pending.
    /// </summary>
    string OldLine { get; }

    /// <summary>
    /// What the launch options become when Replace is pressed. Empty while no replacement is pending.
    /// </summary>
    string NewLine { get; }

    /// <summary>
    /// Whether Replace can be pressed: another manager's line was refused and Steam is closed.
    /// </summary>
    bool CanReplace { get; }

    ReactiveCommand<Unit, Unit> CopyCommand { get; }

    ReactiveCommand<Unit, Unit> WriteCommand { get; }

    /// <summary>
    /// Asks Steam to exit, waits for it, writes the launch option, and starts Steam again.
    /// </summary>
    ReactiveCommand<Unit, Unit> CloseAndWriteCommand { get; }

    /// <summary>
    /// Stops waiting for Steam to exit. Nothing is written.
    /// </summary>
    ReactiveCommand<Unit, Unit> CancelWaitCommand { get; }

    /// <summary>
    /// Swaps <see cref="OldLine"/> for <see cref="NewLine"/> in Steam's config.
    /// </summary>
    ReactiveCommand<Unit, Unit> ReplaceCommand { get; }

    ReactiveCommand<Unit, Unit> CloseCommand { get; }
}
