using R3;

namespace SilverModManager.App.UI.Overlays.Login;

/// <summary>
/// The dialog where the user pastes a secret such as a personal API key. The result is the trimmed value,
/// or null when the user cancelled.
/// </summary>
public interface IApiKeyLoginOverlayViewModel : IOverlayViewModel<string>
{
    BindableReactiveProperty<string> Title { get; }

    BindableReactiveProperty<string> Instructions { get; }

    /// <summary>The hint inside the empty box.</summary>
    BindableReactiveProperty<string> Watermark { get; }

    /// <summary>A page to read the secret from; empty when there is none to show.</summary>
    BindableReactiveProperty<string> PageUrl { get; }

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
