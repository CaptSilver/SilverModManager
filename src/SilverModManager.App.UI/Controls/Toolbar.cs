using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.App.UI.Controls;

public class Toolbar : ItemsControl
{
    private static readonly FuncTemplate<Panel?> DefaultPanel =
        new(() => new StackPanel() { Orientation = Orientation.Horizontal, Spacing = 8 });

    static Toolbar()
    {
        ItemsPanelProperty.OverrideDefaultValue<Toolbar>(DefaultPanel);
    }
}

