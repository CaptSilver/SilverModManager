using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAssertions;
using SilverModManager.App.UI.Controls.DragAndDrop;
using SilverModManager.App.UI.Windows;
using SilverModManager.UI.Tests.Framework;

namespace SilverModManager.UI.Tests.Windows;

/// <summary>The window's drop feedback: the dimmed overlay for the window itself, a caption beside any other zone.</summary>
[Collection(HeadlessUi.Collection)]
public class WindowDropFeedbackTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly DropVerdict Accepted = new DropVerdict.Accept(DragDropEffects.Move, "Move 3 mods to NCR Core");
    private static readonly DropVerdict Refused = new DropVerdict.Refuse("NCR Core is a Nexus collection; it decides what it contains");

    private sealed record Harness(Window Window, Border Entry, Border Page, Border Overlay, TextBlock OverlayText, Border Caption, TextBlock CaptionText, WindowDropFeedback Feedback);

    private async Task<Harness> Show()
    {
        return await OnUi(() =>
        {
            var entry = new Border { Width = 100, Height = 30, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 20, 0, 0) };
            var page = new Border { Width = 300, Height = 150, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
            var overlayText = new TextBlock();
            var overlay = new Border { Child = overlayText, IsVisible = false };
            var captionText = new TextBlock();
            var caption = new Border { Child = captionText, IsVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            var window = Track(new Window { Width = 500, Height = 300, Content = new Grid { Children = { entry, page, overlay, caption } } });
            ShowRendered(window);
            return Task.FromResult(new Harness(window, entry, page, overlay, overlayText, caption, captionText, new WindowDropFeedback(overlay, overlayText, caption, captionText)));
        });
    }

    [Fact]
    public async Task ForTheWindowItself_TheOverlayShowsTheText_AndTheCaptionStaysAway()
    {
        var harness = await Show();

        await OnUi(() =>
        {
            harness.Feedback.Show(harness.Window, Accepted);

            harness.Overlay.IsVisible.Should().BeTrue();
            harness.OverlayText.Text.Should().Be("Move 3 mods to NCR Core");
            harness.Caption.IsVisible.Should().BeFalse();
        });
    }

    [Fact]
    public async Task ForAnotherControl_OnlyTheCaptionShows_AndTheControlGetsTheDropOverPseudoClass()
    {
        var harness = await Show();

        await OnUi(() =>
        {
            harness.Feedback.Show(harness.Entry, Accepted);

            harness.Overlay.IsVisible.Should().BeFalse();
            harness.Caption.IsVisible.Should().BeTrue();
            harness.CaptionText.Text.Should().Be("Move 3 mods to NCR Core");
            harness.Entry.Classes.Should().Contain(":drop-over");
        });
    }

    [Fact]
    public async Task ARefusal_ShowsItsReason_AndMarksTheCaptionAsARefusal()
    {
        var harness = await Show();

        await OnUi(() =>
        {
            harness.Feedback.Show(harness.Entry, Refused);
            harness.CaptionText.Text.Should().Be("NCR Core is a Nexus collection; it decides what it contains");
            harness.Caption.Classes.Should().Contain("refuse");

            harness.Feedback.Show(harness.Entry, Accepted);
            harness.Caption.Classes.Should().NotContain("refuse");
        });
    }

    [Fact]
    public async Task Hide_ClearsTheOverlayTheCaptionAndThePseudoClass()
    {
        var harness = await Show();

        await OnUi(() =>
        {
            harness.Feedback.Show(harness.Window, Accepted);
            harness.Feedback.Show(harness.Entry, Accepted);
            harness.Feedback.Hide();

            harness.Overlay.IsVisible.Should().BeFalse();
            harness.Caption.IsVisible.Should().BeFalse();
            harness.Entry.Classes.Should().NotContain(":drop-over");
        });
    }

    [Fact]
    public async Task MovingToAnotherControl_ClearsThePseudoClassOfTheFirst()
    {
        var harness = await Show();

        await OnUi(() =>
        {
            harness.Feedback.Show(harness.Entry, Accepted);
            harness.Feedback.Show(harness.Page, Accepted);

            harness.Entry.Classes.Should().NotContain(":drop-over");
            harness.Page.Classes.Should().Contain(":drop-over");
        });
    }

    [Fact]
    public async Task ANarrowControl_GetsTheCaptionBesideIt()
    {
        var harness = await Show();

        await OnUi(() =>
        {
            harness.Feedback.Show(harness.Entry, Accepted);

            var entryTop = harness.Entry.TranslatePoint(default, harness.Window)!.Value;
            harness.Caption.Margin.Left.Should().BeGreaterThanOrEqualTo(entryTop.X + harness.Entry.Bounds.Width);
            harness.Caption.Margin.Top.Should().Be(entryTop.Y);
        });
    }

    [Fact]
    public async Task AWideControl_GetsTheCaptionInsideItsTopLeftCorner()
    {
        var harness = await Show();

        await OnUi(() =>
        {
            harness.Feedback.Show(harness.Page, Accepted);

            var pageTop = harness.Page.TranslatePoint(default, harness.Window)!.Value;
            harness.Caption.Margin.Left.Should().BeGreaterThan(pageTop.X);
            harness.Caption.Margin.Left.Should().BeLessThan(pageTop.X + harness.Page.Bounds.Width);
            harness.Caption.Margin.Top.Should().BeGreaterThan(pageTop.Y);
        });
    }
}
