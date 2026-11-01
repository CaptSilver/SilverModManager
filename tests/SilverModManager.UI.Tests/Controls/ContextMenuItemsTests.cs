using Avalonia.Controls;
using FluentAssertions;
using ReactiveUI;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Resources;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.UI.Tests.Controls;

/// <summary>The one place an <see cref="IContextMenuItem"/> becomes an Avalonia menu entry.</summary>
public class ContextMenuItemsTests(IServiceProvider provider) : AUiTest(provider)
{
    private static ContextMenuItem Item(string? toolTip = null, ContextMenuItemStyling styling = ContextMenuItemStyling.Default, IconValue? icon = null) => new()
    {
        Header = "Do it",
        Icon = icon,
        Styling = styling,
        ToolTip = toolTip,
    };

    [Fact]
    public async Task AMenuEntryCarriesTheItemsHeaderCommandIconAndTooltip()
    {
        await OnUi(() =>
        {
            var item = Item("Because", ContextMenuItemStyling.Critical, IconValues.DeleteOutline);

            var entry = item.ToMenuItem();

            entry.Header.Should().Be("Do it");
            entry.Command.Should().BeSameAs(item.Command);
            entry.Icon.Should().BeOfType<UnifiedIcon>().Which.Value.Should().Be(IconValues.DeleteOutline);
            entry.Classes.Should().Contain("Critical");
            ToolTip.GetTip(entry).Should().Be("Because");
        });
    }

    [Fact]
    public async Task AnItemWithNoTooltipOrIcon_GetsNeither()
    {
        await OnUi(() =>
        {
            var entry = Item().ToMenuItem();

            ToolTip.GetTip(entry).Should().BeNull();
            entry.Icon.Should().BeNull();
            entry.Classes.Should().NotContain("Critical");
        });
    }

    [Fact]
    public async Task APremiumItemGetsThePremiumClass()
    {
        await OnUi(() => Item(styling: ContextMenuItemStyling.Premium).ToMenuItem().Classes.Should().Contain("Premium"));
    }

    [Fact]
    public async Task ANavigationControlListsOpenInNewTabAndPanelThenASeparatorThenItsItems()
    {
        await OnUi(() =>
        {
            var control = new NavigationControl { AdditionalContextMenuItems = [Item(), new ContextMenuItem { Header = "Second", IsVisible = false }] };

            var items = control.ContextMenu!.Items.ToList();

            items.Should().HaveCount(4);
            items[0].Should().BeOfType<MenuItem>().Which.Header.Should().Be(Language.NavigationControl_NavigationControl_Open_in_new_tab);
            items[1].Should().BeOfType<MenuItem>().Which.Header.Should().Be(Language.NavigationControl_NavigationControl_Open_in_new_panel);
            items[2].Should().BeOfType<Separator>();
            items[3].Should().BeOfType<MenuItem>().Which.Header.Should().Be("Do it");
        });
    }
}
