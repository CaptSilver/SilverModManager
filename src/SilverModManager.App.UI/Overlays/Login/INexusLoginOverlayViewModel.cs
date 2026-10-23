namespace SilverModManager.App.UI.Overlays.Login;

public interface INexusLoginOverlayViewModel : IOverlayViewModel
{
    public R3.ReactiveCommand Cancel { get; }

    /// <summary>
    /// Abandons the browser flow and opens the API key dialog.
    /// </summary>
    public R3.ReactiveCommand<R3.Unit> UseApiKey { get; }
    public Uri? Uri { get; }
}
