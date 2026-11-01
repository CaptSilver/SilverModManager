using Avalonia;
using Avalonia.Controls;

namespace SilverModManager.App.UI.Controls.DragAndDrop;

/// <summary>Shows the user what a drag over a zone will do, or why it won't.</summary>
public interface IDropFeedback
{
    /// <param name="over">The zone's host control.</param>
    /// <param name="verdict">An <see cref="DropVerdict.Accept"/> or a <see cref="DropVerdict.Refuse"/>.</param>
    void Show(Control over, DropVerdict verdict);

    void Hide();
}

/// <summary>Where zones find the window's <see cref="IDropFeedback"/> when they are not given one.</summary>
public static class DropFeedback
{
    /// <summary>Set on the window; a zone reads it from its host's top level at the first drag event.</summary>
    public static readonly AttachedProperty<IDropFeedback?> LayerProperty =
        AvaloniaProperty.RegisterAttached<TopLevel, IDropFeedback?>("Layer", typeof(DropFeedback));

    public static IDropFeedback? GetLayer(TopLevel topLevel) => topLevel.GetValue(LayerProperty);

    public static void SetLayer(TopLevel topLevel, IDropFeedback? value) => topLevel.SetValue(LayerProperty, value);
}
