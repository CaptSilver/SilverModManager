using Avalonia.Controls;
using Avalonia.Media;
using FluentAssertions;
using SilverModManager.App.UI.LeftMenu.Items;

namespace SilverModManager.UI.Tests.LeftMenu;

/// <summary>The Play/Apply box sits on the left menu panel, which Glass tints; an opaque box would show as a dark block.</summary>
public class ApplyControlGlassTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public async Task TheApplyBoxIsAWashOverTheLeftMenuRatherThanAnOpaqueBlock()
    {
        await OnUi(() =>
        {
            var view = new ApplyControlView { ViewModel = new ApplyControlDesignViewModel() };
            var window = Track(new Window { Width = 260, Height = 200, Content = view });
            ShowRendered(window);

            var box = view.FindControl<Border>("ApplyControlBorder");
            box.Should().NotBeNull();
            var color = box!.Background.Should().BeAssignableTo<ISolidColorBrush>().Subject.Color;
            color.A.Should().BeInRange(1, 254, "the box is lifted off the panel but lets its Glass tint through");
        });
    }
}
