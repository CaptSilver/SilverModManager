using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentAssertions;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Controls.TopBar;

namespace SilverModManager.UI.Tests.Theme;

/// <summary>
/// Large surfaces inside the left menu and page panels, which Glass tints. Painted opaque, each shows as a solid block
/// over the blurred desktop, so they are washes or transparent instead.
/// </summary>
public class GlassSurfacesTests(IServiceProvider provider) : AUiTest(provider)
{
    private static byte? Alpha(IBrush? brush) => (brush as ISolidColorBrush)?.Color.A;

    [Fact]
    public async Task ATableLetsThePanelShowThrough()
    {
        await OnUi(() =>
        {
            var grid = new TreeDataGrid();
            ShowRendered(Track(new Window { Width = 400, Height = 300, Content = grid }));

            Alpha(grid.Background).Should().NotBe(255);
        });
    }

    [Theory]
    [InlineData("selected")]
    [InlineData("active")]
    public async Task ALeftMenuItemsHighlightIsAWash(string state)
    {
        await OnUi(() =>
        {
            var item = state == "selected" ? new LeftMenuNavButton { IsSelected = true } : new LeftMenuNavButton { IsActive = true };
            ShowRendered(Track(new Window { Width = 300, Height = 100, Content = item }));

            Alpha(item.Background).Should().BeInRange(1, 254, $"a {state} item is lifted off the panel but lets Glass through");
        });
    }

    [Fact]
    public async Task TheWindowButtonsSitOnTheWindowBaseRatherThanABlock()
    {
        await OnUi(() =>
        {
            var topBar = new TopBarView();
            ShowRendered(Track(new Window { Width = 1000, Height = 100, Content = topBar }));

            Alpha(topBar.FindControl<Border>("SystemButtonsBorder")!.Background).GetValueOrDefault().Should()
                .Be(0, "the minimise, maximise and close buttons show the blurred desktop like the rest of the top bar");
            topBar.FindControl<Grid>("MainGrid")!.ClipToBounds.Should()
                .BeTrue("in a narrow window the top bar is cut off at the buttons instead of drawing under them");
        });
    }

    [Fact]
    public async Task TheAddPanelDropDownsChevronIsAWash()
    {
        await OnUi(() =>
        {
            var dropDown = new ComboBox { Classes = { "AddPanelDropDown" } };
            ShowRendered(Track(new Window { Width = 200, Height = 60, Content = dropDown }));

            var overlay = dropDown.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "DropDownOverlay");
            Alpha(overlay.Background).Should().BeInRange(1, 254, "the chevron's circle is visible but lets Glass through");
        });
    }
}
