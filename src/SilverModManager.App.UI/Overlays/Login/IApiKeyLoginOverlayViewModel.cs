using R3;

namespace NexusMods.App.UI.Overlays.Login;

/// <summary>
/// The dialog where the user pastes a personal Nexus Mods API key. The result is the trimmed key,
/// or null when the user cancelled.
/// </summary>
public interface IApiKeyLoginOverlayViewModel : IOverlayViewModel<string>
{
    BindableReactiveProperty<string> ApiKey { get; }

    /// <summary>
    /// Why the last key was refused; empty when there is nothing to show.
    /// </summary>
    BindableReactiveProperty<string> ErrorMessage { get; }

    /// <summary>
    /// Disabled while the box holds nothing but whitespace.
    /// </summary>
    ReactiveCommand<Unit> CommandSave { get; }

    ReactiveCommand<Unit> CommandCancel { get; }
}
