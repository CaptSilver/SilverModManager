using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace SilverModManager.App.UI.Windows.Xdnd;

/// <summary>
/// Raises <see cref="DragDrop"/>'s DragEnter, DragOver, DragLeave and Drop events on a window the way Avalonia's own
/// drag device does for a drag it receives natively, through public API only. The effect each call returns is the
/// window's answer. Mirrors the device's target rules exactly, because the drop zones are written against them:
/// the target is the one control under the point and only if it allows drops (no walking up to an ancestor that
/// does), and Drop goes to the last target without a new hit test.
/// </summary>
public sealed class AvaloniaDragRouter(TopLevel root) : IXdndSink
{
    // XDND carries the action, not the modifier state, and the drop zones don't read modifiers.
    private const KeyModifiers Modifiers = KeyModifiers.None;

    private Interactive? _lastTarget;

    // What the last Enter or Over carried, for the DragLeave that follows it: the device hands Leave the same point,
    // data and effects, and the sink's Leave has none of its own.
    private (Point Point, IDataObject Data, DragDropEffects Allowed) _last;

    public DragDropEffects Enter(PixelPoint at, IDataObject data, DragDropEffects allowed)
    {
        var point = ToDips(at);
        _last = (point, data, allowed);
        _lastTarget = TargetAt(point);
        return Raise(_lastTarget, point, DragDrop.DragEnterEvent, data, allowed);
    }

    public DragDropEffects Over(PixelPoint at, IDataObject data, DragDropEffects allowed)
    {
        var point = ToDips(at);
        var target = TargetAt(point);
        _last = (point, data, allowed);
        if (target == _lastTarget) return Raise(target, point, DragDrop.DragOverEvent, data, allowed);

        // Moving between controls leaves the old one and enters the new one; the remembered target changes even
        // when a handler throws, as in the device.
        try
        {
            if (_lastTarget is not null) Raise(_lastTarget, point, DragDrop.DragLeaveEvent, data, allowed);
            return Raise(target, point, DragDrop.DragEnterEvent, data, allowed);
        }
        finally
        {
            _lastTarget = target;
        }
    }

    public void Leave()
    {
        if (_lastTarget is not { } target) return;
        try
        {
            Raise(target, _last.Point, DragDrop.DragLeaveEvent, _last.Data, _last.Allowed);
        }
        finally
        {
            _lastTarget = null;
        }
    }

    public DragDropEffects Drop(PixelPoint at, IDataObject data, DragDropEffects allowed)
    {
        try
        {
            return Raise(_lastTarget, ToDips(at), DragDrop.DropEvent, data, allowed);
        }
        finally
        {
            _lastTarget = null;
        }
    }

    private Point ToDips(PixelPoint at) => new(at.X / root.RenderScaling, at.Y / root.RenderScaling);

    private Interactive? TargetAt(Point point)
    {
        var hit = (root.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().OfType<Interactive>().FirstOrDefault();
        return hit is not null && DragDrop.GetAllowDrop(hit) ? hit : null;
    }

    private DragDropEffects Raise(Interactive? target, Point point, RoutedEvent<DragEventArgs> routedEvent, IDataObject data, DragDropEffects effects)
    {
        if (target is null) return DragDropEffects.None;
        if (root.TranslatePoint(point, target) is not { } local) return DragDropEffects.None;

        // DragEventArgs' constructor is [Obsolete] and [Unstable] ("might be removed in 12.0"). It is the only way for
        // app code to raise drag events: the device and RawDragEvent that normally do are private API.
#pragma warning disable CS0618
        var args = new DragEventArgs(routedEvent, data, target, local, Modifiers) { RoutedEvent = routedEvent, DragEffects = effects };
#pragma warning restore CS0618
        target.RaiseEvent(args);
        return args.DragEffects;
    }
}
