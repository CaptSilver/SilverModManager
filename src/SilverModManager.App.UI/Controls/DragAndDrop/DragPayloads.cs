using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace SilverModManager.App.UI.Controls.DragAndDrop;

/// <summary>Puts a <see cref="DragPayload"/> into Avalonia's data object and reads it back out.</summary>
public static class DragPayloads
{
    /// <summary>
    /// Custom in-process format; the value is the payload itself. Avalonia hands the same data object to every event
    /// of an in-app drag, so nothing is serialized.
    /// </summary>
    public const string Format = "SilverModManager.DragPayload";

    public static DataObject ToDataObject(DragPayload payload)
    {
        var data = new DataObject();
        data.Set(Format, payload);
        return data;
    }

    /// <summary>
    /// The payload a data object carries: our format first, else <see cref="DataFormats.Files"/> as
    /// <see cref="DragPayload.DroppedFiles"/> (local paths only), else null. The only place that reads
    /// <c>DataFormats.Files</c>.
    /// </summary>
    public static DragPayload? From(IDataObject data)
    {
        if (data.Contains(Format) && data.Get(Format) is DragPayload payload) return payload;

        var paths = data.GetFiles()?
            .Select(static item => item.TryGetLocalPath())
            .OfType<string>()
            .ToArray() ?? [];
        return paths.Length == 0 ? null : new DragPayload.DroppedFiles(paths);
    }
}
