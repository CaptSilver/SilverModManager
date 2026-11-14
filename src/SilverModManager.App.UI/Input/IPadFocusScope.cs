using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Primitives;

namespace SilverModManager.App.UI.Input;

/// <summary>
/// A control that uses some D-pad directions itself: a list moves its selection on up and down, a text box moves its
/// caret on left and right. The pad sends those directions to the control and does not move focus with them.
/// </summary>
public interface IPadFocusScope
{
    bool WantsDirection(NavigationDirection direction);
}

/// <summary>Finds the scope that covers the focused element.</summary>
public static class PadFocusScopes
{
    private sealed class Directions(params NavigationDirection[] wanted) : IPadFocusScope
    {
        public bool WantsDirection(NavigationDirection direction) => wanted.Contains(direction);
    }

    private static readonly IPadFocusScope List = new Directions(NavigationDirection.Up, NavigationDirection.Down);
    private static readonly IPadFocusScope Text = new Directions(NavigationDirection.Left, NavigationDirection.Right);
    private static readonly IPadFocusScope MultilineText = new Directions(NavigationDirection.Left, NavigationDirection.Right, NavigationDirection.Up, NavigationDirection.Down);

    private static StyledElement OwnerOfList(StyledElement element) =>
        // An item reports to the list around it: the list's own key handling is what moves the selection.
        element is ListBoxItem or ComboBoxItem ? FocusAncestors.Of(element as IInputElement).FirstOrDefault(e => e is ListBox or ComboBox) ?? element : element;

    /// <summary>A scope together with the control that owns it.</summary>
    public sealed record Owned(IPadFocusScope Scope, StyledElement Owner);

    /// <summary>
    /// The scope of the nearest control that has one, or null when every direction moves focus. Controls the app does not
    /// own (lists, tree grids, text boxes, the items of an open combo box) are covered here; a closed combo box is not,
    /// because up and down on it would change its selection and trap the pad.
    /// </summary>
    public static Owned? For(IInputElement? focused)
    {
        foreach (var element in FocusAncestors.Of(focused))
        {
            switch (element)
            {
                case IPadFocusScope scope: return new Owned(scope, element);
                case TextBox { AcceptsReturn: true }: return new Owned(MultilineText, element);
                case TextBox: return new Owned(Text, element);
                case ComboBox: return null;
                case ListBoxItem or ComboBoxItem or ListBox or TreeDataGrid or DataGrid: return new Owned(List, OwnerOfList(element));
            }
        }

        return null;
    }
}
