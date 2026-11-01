using Avalonia;
using Avalonia.Input;
using SilverModManager.App.UI.Windows.Xdnd;

namespace SilverModManager.UI.Tests.Windows.Xdnd;

public sealed record SinkCall(string Kind, PixelPoint At = default, IDataObject? Data = null, DragDropEffects Allowed = DragDropEffects.None);

/// <summary>An IXdndSink that records every call and answers with the effect a test sets.</summary>
public sealed class RecordingSink : IXdndSink
{
    public List<SinkCall> Calls { get; } = [];

    /// <summary>What Enter, Over and Drop return; Copy unless a test says otherwise.</summary>
    public DragDropEffects Answer { get; set; } = DragDropEffects.Copy;

    public IEnumerable<string> Kinds => Calls.Select(c => c.Kind);

    public DragDropEffects Enter(PixelPoint at, IDataObject data, DragDropEffects allowed) => Record("Enter", at, data, allowed);

    public DragDropEffects Over(PixelPoint at, IDataObject data, DragDropEffects allowed) => Record("Over", at, data, allowed);

    public void Leave() => Calls.Add(new SinkCall("Leave"));

    public DragDropEffects Drop(PixelPoint at, IDataObject data, DragDropEffects allowed) => Record("Drop", at, data, allowed);

    private DragDropEffects Record(string kind, PixelPoint at, IDataObject data, DragDropEffects allowed)
    {
        Calls.Add(new SinkCall(kind, at, data, allowed));
        return Answer;
    }
}
