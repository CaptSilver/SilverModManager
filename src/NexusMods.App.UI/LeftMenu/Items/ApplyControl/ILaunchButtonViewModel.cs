using System.Reactive;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Loadouts;
using NexusMods.UI.Sdk;
using ReactiveUI;

namespace NexusMods.App.UI.LeftMenu.Items;

public interface ILaunchButtonViewModel : IViewModelInterface
{
    /// <summary>
    /// The currently selected loadout.
    /// </summary>
    public LoadoutId LoadoutId { get; set; }

    /// <summary>
    /// The command to execute when the button is clicked.
    /// </summary>
    public ReactiveCommand<Unit, Unit> Command { get; set; }

    /// <summary>
    /// Returns an observable which signals whether the game is currently running.
    /// This signals the initials state immediately upon subscribing.
    /// </summary>
    public IObservable<bool> IsRunningObservable { get; }

    /// <summary>
    /// Text to display on the button.
    /// </summary>
    public string Label { get; }
    public Percent? Progress { get; }

    /// <summary>
    /// True while the launch has handed the game to Steam and Steam has not started it yet. Nothing in Steam
    /// tells us when the user backs out there, so this is the phase <see cref="CancelCommand"/> is for.
    /// </summary>
    public bool IsWaitingForSteam { get; }

    /// <summary>
    /// Stops waiting for Steam and releases the launch. Only available while <see cref="IsWaitingForSteam"/>.
    /// </summary>
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
}
