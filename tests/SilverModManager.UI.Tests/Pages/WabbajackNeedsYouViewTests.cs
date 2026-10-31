using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>Renders the modlist page headlessly and reads its needs-you section, filter and "Show them" action.</summary>
public class WabbajackNeedsYouViewTests(IServiceProvider provider) : AUiTest(provider)
{
    private static WabbajackArchiveRow Archive(ArchiveState state, string name, string? reason = null)
        => new(new ArchivePlanRow(new ArchiveEntry(FileIdentity.XxHash64(Hash.From((ulong)name.GetHashCode())), 4_200_000, name, "", new ManualState(new Uri("https://example.com/a"), "get it")),
            state, reason, new Uri("https://example.com/files/1"), null));

    private async Task<(Window Window, WabbajackModlistView View)> Show(WabbajackModlistDesignViewModel viewModel)
    {
        var (window, view) = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var view = new WabbajackModlistView { DataContext = viewModel };
            var window = Track(new Window { Width = 1200, Height = 900, Content = view });
            ShowRendered(window);
            return (window, view);
        });
        await EventuallyOnUi(() =>
        {
            Render(window);
            view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Should().Contain("Archives", "the page has to bind before it is read");
        });
        return (window, view);
    }

    private static T Named<T>(Control root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [Fact]
    public async Task WithNothingToDo_TheSectionIsHidden()
    {
        var (window, view) = await Show(new WabbajackModlistDesignViewModel { Archives = [Archive(ArchiveState.Have, "a.7z"), Archive(ArchiveState.Automatic, "b.7z")] });

        await OnUi(() =>
        {
            Named<StackPanel>(view, "NeedsYouPanel").IsEffectivelyVisible.Should().BeFalse();
            Named<StackPanel>(view, "ArchivesPanel").IsEffectivelyVisible.Should().BeTrue();
        });
        await OnUi(window.Close);
    }

    [Fact]
    public async Task WithRowsThatNeedTheUser_TheSectionSitsAboveTheTable_WithItsHeadingAndCopyButton()
    {
        var (window, view) = await Show(new WabbajackModlistDesignViewModel
        {
            Archives = [Archive(ArchiveState.Have, "held.7z"), Archive(ArchiveState.Manual, "Hand Made.zip", "hash mismatch"), Archive(ArchiveState.Browser, "Textures Pack.7z")],
        });

        await EventuallyOnUi(() =>
        {
            Render(window);
            var panel = Named<StackPanel>(view, "NeedsYouPanel");
            panel.IsEffectivelyVisible.Should().BeTrue();
            Named<TextBlock>(view, "NeedsYouHeading").Text.Should().Be("2 archives need you");
            Named<StandardButton>(view, "CopyListButton").IsEffectivelyVisible.Should().BeTrue();
            panel.GetVisualDescendants().OfType<WabbajackArchiveLine>().Should().HaveCount(2);
            panel.TranslatePoint(default, view)!.Value.Y.Should().BeLessThan(Named<StackPanel>(view, "ArchivesPanel").TranslatePoint(default, view)!.Value.Y);
        });
        await OnUi(window.Close);
    }

    [Fact]
    public async Task ShowThem_IsBesideAPlainStatusLine_AndGoneWhileTheListIsBeingChecked()
    {
        var (window, view) = await Show(new WabbajackModlistDesignViewModel { CanShowMissing = true });
        await OnUi(() => Named<StandardButton>(view, "ShowThemButton").IsEffectivelyVisible.Should().BeTrue());
        await OnUi(window.Close);

        var (checking, checkingView) = await Show(new WabbajackModlistDesignViewModel { CanShowMissing = true, IsPlanning = true, CheckText = "Hashing game files: 3.2 of 15.1 GB", CheckFraction = 0.2 });
        await OnUi(() => Named<StandardButton>(checkingView, "ShowThemButton").IsEffectivelyVisible.Should().BeFalse());
        await OnUi(checking.Close);

        var (none, noneView) = await Show(new WabbajackModlistDesignViewModel { CanShowMissing = false });
        await OnUi(() => Named<StandardButton>(noneView, "ShowThemButton").IsEffectivelyVisible.Should().BeFalse());
        await OnUi(none.Close);
    }

    [Fact]
    public async Task ThePickedFilter_ReachesTheViewModel()
    {
        var viewModel = new WabbajackModlistDesignViewModel();
        var (window, view) = await Show(viewModel);

        await OnUi(() => Named<ComboBox>(view, "ArchiveFilterBox").SelectedIndex = (int)WabbajackArchiveFilter.Missing);

        await EventuallyOnUi(() => viewModel.ArchiveFilter.Should().Be(WabbajackArchiveFilter.Missing));
        await OnUi(window.Close);
    }
}
