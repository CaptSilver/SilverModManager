using Avalonia;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace SilverModManager.App.UI.Input;

internal static class FocusAncestors
{
    /// <summary>The focused element and everything above it, through the logical tree where a popup or template breaks the visual one.</summary>
    public static IEnumerable<StyledElement> Of(IInputElement? focused)
    {
        for (var element = focused as StyledElement; element is not null; element = element.Parent ?? (element as Visual)?.GetVisualParent() as StyledElement)
            yield return element;
    }

    public static T? Nearest<T>(IInputElement? focused) where T : class => Of(focused).OfType<T>().FirstOrDefault();
}
