using Avalonia.Controls;
using Avalonia.VisualTree;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>Reads a rendered page the way a person would: what is shown, not what is merely in the tree.</summary>
internal static class ViewProbe
{
    /// <summary>The texts a person can read; a hidden control is still in the tree but is not one of them.</summary>
    public static string[] ShownTexts(Control root) => root.GetVisualDescendants().OfType<TextBlock>()
        .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text))
        .Select(t => t.Text!)
        .ToArray();

    public static T Named<T>(Control root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
}
