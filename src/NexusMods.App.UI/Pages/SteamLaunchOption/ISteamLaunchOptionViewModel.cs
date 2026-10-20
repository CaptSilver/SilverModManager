using System.Reactive;
using NexusMods.App.UI.Overlays;
using ReactiveUI;

namespace NexusMods.App.UI.Pages.SteamLaunchOption;

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
    /// Asks the user to close Steam while it runs, empty otherwise.
    /// </summary>
    string WriteNote { get; }

    /// <summary>
    /// The outcome of the last Copy or Write, empty before either was pressed.
    /// </summary>
    string Message { get; }

    ReactiveCommand<Unit, Unit> CopyCommand { get; }

    ReactiveCommand<Unit, Unit> WriteCommand { get; }

    ReactiveCommand<Unit, Unit> CloseCommand { get; }
}
