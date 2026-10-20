using Avalonia;
using Avalonia.Controls;
using FluentAssertions;
using NexusMods.App.UI.Windows;

namespace NexusMods.UI.Tests.Windows;

public class WindowChromeDragSurfaceTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public async Task BoundaryItself_IsDraggable()
    {
        await OnUi(() =>
        {
            var boundary = new Border();
            WindowChrome.IsEmptyDragSurface(boundary, boundary).Should().BeTrue();
        });
    }

    [Fact]
    public async Task PlainPanelInsideBoundary_IsDraggable()
    {
        await OnUi(() =>
        {
            var panel = new StackPanel();
            var boundary = new Border { Child = new Grid { Children = { panel } } };
            WindowChrome.IsEmptyDragSurface(panel, boundary).Should().BeTrue();
        });
    }

    [Fact]
    public async Task Button_IsNot()
    {
        await OnUi(() =>
        {
            var button = new Button();
            var boundary = new Border { Child = new StackPanel { Children = { button } } };
            WindowChrome.IsEmptyDragSurface(button, boundary).Should().BeFalse();
        });
    }

    [Fact]
    public async Task TextBlock_IsNot()
    {
        await OnUi(() =>
        {
            var text = new TextBlock { Text = "Title" };
            var boundary = new Border { Child = text };
            WindowChrome.IsEmptyDragSurface(text, boundary).Should().BeFalse();
        });
    }

    [Fact]
    public async Task FocusableControl_IsNot()
    {
        await OnUi(() =>
        {
            var tab = new TabItem();
            var boundary = new Border { Child = new StackPanel { Children = { tab } } };
            WindowChrome.IsEmptyDragSurface(tab, boundary).Should().BeFalse();
        });
    }

    [Fact]
    public async Task SourceOutsideBoundary_IsNot()
    {
        await OnUi(() =>
        {
            WindowChrome.IsEmptyDragSurface(new Border(), new Border()).Should().BeFalse();
        });
    }

    [Fact]
    public async Task NullSource_IsNot()
    {
        await OnUi(() =>
        {
            WindowChrome.IsEmptyDragSurface(null, new Border()).Should().BeFalse();
        });
    }

    [Fact]
    public async Task MainWindow_HasNoSystemDecorations()
    {
        await OnUi(() =>
        {
            var window = new MainWindow();
            window.SystemDecorations.Should().Be(SystemDecorations.None);
            window.ExtendClientAreaToDecorationsHint.Should().BeFalse();
        });
    }
}
