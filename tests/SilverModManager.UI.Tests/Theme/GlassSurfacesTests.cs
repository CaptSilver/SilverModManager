using Avalonia.Controls;
using Avalonia.Media;
using FluentAssertions;
using SilverModManager.App.UI.Controls.Navigation;

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
}
