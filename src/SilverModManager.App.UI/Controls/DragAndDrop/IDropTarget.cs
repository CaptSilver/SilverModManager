namespace SilverModManager.App.UI.Controls.DragAndDrop;

/// <summary>What a place accepts from a drag, and what it does with it. A <see cref="DropZone"/> wires one to Avalonia.</summary>
public interface IDropTarget
{
    /// <summary>
    /// Cheap and pure: called on DragEnter and DragOver. The zone caches the verdict per data object, so this runs
    /// once per drag per zone.
    /// </summary>
    DropVerdict Inspect(DragPayload payload);

    /// <summary>Does the transfer. Errors are the caller's to show; the zone reports them through <see cref="IErrorPresenter"/>.</summary>
    Task DropAsync(DragPayload payload, CancellationToken cancellationToken);
}
