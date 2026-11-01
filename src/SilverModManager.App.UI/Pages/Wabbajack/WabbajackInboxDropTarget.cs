using Avalonia.Input;
using SilverModManager.App.UI.Controls.DragAndDrop;

namespace SilverModManager.App.UI.Pages.Wabbajack;

/// <summary>The modlist page's inbox as a drop target: archives dropped anywhere on the page are copied into the inbox.</summary>
public sealed class WabbajackInboxDropTarget(IWabbajackModlistViewModel page) : IDropTarget
{
    public DropVerdict Inspect(DragPayload payload)
    {
        if (payload is not DragPayload.DroppedFiles dropped) return new DropVerdict.Ignore();

        var files = dropped.Paths.Count(File.Exists);
        if (files > 0) return new DropVerdict.Accept(DragDropEffects.Copy, $"Add {files} {(files == 1 ? "file" : "files")} to the inbox");

        return dropped.Paths.All(Directory.Exists)
            ? new DropVerdict.Refuse("Drop the archives, not the folder")
            : new DropVerdict.Refuse("These files don't exist any more");
    }

    public Task DropAsync(DragPayload payload, CancellationToken cancellationToken)
        => payload is DragPayload.DroppedFiles dropped ? page.AddDroppedFilesAsync(dropped.Paths, cancellationToken) : Task.CompletedTask;
}
