namespace SilverModManager.App.UI.Overlays;

/// <summary>
/// The controller design-time overlay view models hold. The previewer only draws them, so asking it to show,
/// await or remove an overlay is a mistake and says so.
/// </summary>
public sealed class DesignOverlayController : IOverlayController
{
    private const string DesignTimeOnly = "design-time only";

    /// <inheritdoc/>
    public IOverlayViewModel? CurrentOverlay => throw new NotSupportedException(DesignTimeOnly);

    /// <inheritdoc/>
    public void Enqueue(IOverlayViewModel overlayViewModel) => throw new NotSupportedException(DesignTimeOnly);

    /// <inheritdoc/>
    public Task<TResult?> EnqueueAndWait<TResult>(IOverlayViewModel<TResult> overlayViewModel) => throw new NotSupportedException(DesignTimeOnly);

    /// <inheritdoc/>
    public void Remove(IOverlayViewModel model) => throw new NotSupportedException(DesignTimeOnly);
}
