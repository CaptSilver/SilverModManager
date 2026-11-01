using Avalonia.Controls;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.App.UI.Controls.Navigation;

public static class ContextMenuItems
{
    /// <summary>The Avalonia menu entry for <paramref name="item"/>: header, command, icon, styling class and tooltip.</summary>
    public static MenuItem ToMenuItem(this IContextMenuItem item)
    {
        var menuItem = new MenuItem
        {
            Header = item.Header,
            Command = item.Command,
        };

        if (item.Icon != null)
        {
            menuItem.Icon = new UnifiedIcon
            {
                Value = item.Icon,
                Size = 16,
            };
        }

        if (item.ToolTip is { } toolTip)
            ToolTip.SetTip(menuItem, toolTip);

        switch (item.Styling)
        {
            case ContextMenuItemStyling.Critical:
                menuItem.Classes.Add("Critical");
                break;
            case ContextMenuItemStyling.Premium:
                menuItem.Classes.Add("Premium");
                break;
            case ContextMenuItemStyling.None:
            case ContextMenuItemStyling.Default:
            default:
                // No additional classes for default styling
                break;
        }

        return menuItem;
    }
}
