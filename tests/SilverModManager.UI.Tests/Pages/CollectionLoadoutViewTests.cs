using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using NexusMods.Paths;
using ReactiveUI;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Sdk.Jobs;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Pages.Collections;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>Renders the real collection page headlessly and reads what a person would see.</summary>
public class CollectionLoadoutViewTests(IServiceProvider provider) : AUiTest(provider)
{
    // Long enough to wrap past three lines at the test window's width, so the page has to collapse it.
    private static readonly string TwoParagraphs = string.Join("\n\n", Enumerable.Repeat("A big Skyrim VR overhaul. Read the readme before installing; it needs a lot of room and a fast machine, and the first install takes an afternoon.", 5));

    private async Task<(Window Window, CollectionLoadoutView View)> Show(CollectionLoadoutDesignViewModel viewModel, double width = 1200)
    {
        var (window, view) = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var view = new CollectionLoadoutView { DataContext = viewModel };
            var window = Track(new Window { Width = width, Height = 900, Content = view });
            ShowRendered(window);
            return (window, view);
        });
        await EventuallyOnUi(() =>
        {
            Render(window);
            ViewProbe.ShownTexts(view).Should().Contain(viewModel.Name, "the page has to bind before it is read");
        });
        return (window, view);
    }

    private static T Named<T>(Control root, string name) where T : Control => ViewProbe.Named<T>(root, name);

    private static CollectionStats FullStats { get; } =
        new(Percent.CreateClamped(0.75), 1234UL, 99_000UL, Size.From(5_000_000UL), null);

    [Fact]
    public async Task TheSearchBoxFiltersTheInstalledTreeThroughItsAdapter()
    {
        var viewModel = new CollectionLoadoutDesignViewModel();
        var (window, view) = await Show(viewModel);
        var search = Named<SilverModManager.App.UI.Controls.Search.SearchControl>(view, "SearchControl");

        await OnUi(() => search.Adapter.Should().BeSameAs(viewModel.Adapter));
        await OnUi(() => Named<TextBox>(view, "SearchTextBox").Text = "Upscaler");

        await EventuallyOnUi(() => viewModel.Adapter!.Filter.Value.Should().Be(new SilverModManager.App.UI.Controls.Filters.Filter.TextFilter("Upscaler")));
        await OnUi(window.Close);
    }

    [Fact]
    public async Task NoModsMatchShowsOnlyWhileTheAdapterReportsNoMatches()
    {
        var viewModel = new CollectionLoadoutDesignViewModel();
        var (window, view) = await Show(viewModel);
        await OnUi(() => Named<TextBlock>(view, "NoMatchesText").IsEffectivelyVisible.Should().BeFalse());

        await OnUi(() => viewModel.Adapter!.HasNoMatches.Value = true);
        await EventuallyOnUi(() =>
        {
            Render(window);
            Named<TextBlock>(view, "NoMatchesText").IsEffectivelyVisible.Should().BeTrue();
            Named<TextBlock>(view, "NoMatchesText").Text.Should().Be("No mods match");
        });

        await OnUi(() => viewModel.Adapter!.HasNoMatches.Value = false);
        await EventuallyOnUi(() => Named<TextBlock>(view, "NoMatchesText").IsEffectivelyVisible.Should().BeFalse());
        await OnUi(window.Close);
    }

    [Fact]
    public async Task ALongDescription_IsCollapsedUntilShowMoreIsPressed()
    {
        var (window, view) = await Show(new CollectionLoadoutDesignViewModel { Description = TwoParagraphs });

        // The toggle is decided from the laid-out text, which exists a turn after the page binds.
        await EventuallyOnUi(() =>
        {
            Named<TextBlock>(view, "DescriptionText").MaxLines.Should().Be(3);
            ViewProbe.ShownTexts(view).Should().Contain("Show more");
            Click_AlreadyOnUi(Named<StandardButton>(view, "DescriptionToggle"));
            Render(window);
            Named<TextBlock>(view, "DescriptionText").MaxLines.Should().Be(0);
            ViewProbe.ShownTexts(view).Should().Contain("Show less");
        });
        await OnUi(window.Close);
    }

    [Fact]
    public async Task AShortDescription_HasNoToggle_AndNoDescriptionHidesTheBlock()
    {
        var (window, view) = await Show(new CollectionLoadoutDesignViewModel { Description = "A small list." });
        await OnUi(() =>
        {
            Named<StackPanel>(view, "DescriptionPanel").IsEffectivelyVisible.Should().BeTrue();
            Named<StandardButton>(view, "DescriptionToggle").IsEffectivelyVisible.Should().BeFalse();
        });
        await OnUi(window.Close);

        var (bare, bareView) = await Show(new CollectionLoadoutDesignViewModel { Description = "" });
        await OnUi(() => Named<StackPanel>(bareView, "DescriptionPanel").IsEffectivelyVisible.Should().BeFalse());
        await OnUi(bare.Close);
    }

    [Fact]
    public async Task ALongSingleParagraph_ThatWrapsPastThreeLinesInANarrowWindow_GetsShowMore()
    {
        var text = "A big Skyrim VR overhaul with new textures, combat, quests and a lot of extra scripts that need room and a fast machine to run. " +
                   "Read the readme first, then give the first install an afternoon, and keep the game files on a fast drive.";
        text.Length.Should().BeLessThan(240, "a character count alone would call this short");
        var (window, view) = await Show(new CollectionLoadoutDesignViewModel { Description = text }, width: 420);

        await EventuallyOnUi(() => ViewProbe.ShownTexts(view).Should().Contain("Show more"));
        await OnUi(window.Close);
    }

    [Fact]
    public async Task ADescriptionThatFitsInThreeLines_HasNoToggle_EvenWithABlankLine()
    {
        var (window, view) = await Show(new CollectionLoadoutDesignViewModel { Description = "One short line.\n\nAnother short line." });

        await OnUi(() => Named<StandardButton>(view, "DescriptionToggle").IsEffectivelyVisible.Should().BeFalse());
        await OnUi(window.Close);
    }

    [Fact]
    public async Task WithoutABanner_TheImageSlotIsGone_SoTheTitleAlignsWithTheRest()
    {
        var (window, view) = await Show(new CollectionLoadoutDesignViewModel { TileImage = null });

        await OnUi(() => Named<Border>(view, "CollectionImageBorder").IsEffectivelyVisible.Should().BeFalse());
        await OnUi(window.Close);
    }

    [Fact]
    public async Task APanelWhoseStatIsNullIsHiddenAndTheOthersStay()
    {
        var (window, view) = await Show(new CollectionLoadoutDesignViewModel { Stats = new CollectionStats(null, null, null, Size.From(5_000_000UL), null) });

        await OnUi(() =>
        {
            Named<StackPanel>(view, "TotalSizePanel").IsEffectivelyVisible.Should().BeTrue();
            foreach (var hidden in new[] { "OverallRatingPanel", "EndorsementsPanel", "DownloadsPanel", "ArchivesPanel" })
                Named<StackPanel>(view, hidden).IsEffectivelyVisible.Should().BeFalse($"{hidden} has no number to show");
        });
        await OnUi(window.Close);
    }

    [Fact]
    public async Task ANexusShapedHeaderShowsItsFourNumbersAndNotTheArchiveCount()
    {
        var (window, view) = await Show(new CollectionLoadoutDesignViewModel { Stats = FullStats });

        await OnUi(() =>
        {
            foreach (var shown in new[] { "OverallRatingPanel", "EndorsementsPanel", "DownloadsPanel", "TotalSizePanel" })
                Named<StackPanel>(view, shown).IsEffectivelyVisible.Should().BeTrue($"{shown} has a number");
            Named<StackPanel>(view, "ArchivesPanel").IsEffectivelyVisible.Should().BeFalse("Nexus counts no archives");
        });
        await OnUi(window.Close);
    }

    [Fact]
    public async Task AnArchiveCountShowsTheArchivesPanel()
    {
        var (window, view) = await Show(new CollectionLoadoutDesignViewModel { Stats = new CollectionStats(null, null, null, Size.From(5_000_000UL), 312) });

        await OnUi(() =>
        {
            Named<StackPanel>(view, "ArchivesPanel").IsEffectivelyVisible.Should().BeTrue();
            Named<TextBlock>(view, "ArchiveCountText").Text.Should().Contain("312");
        });
        await OnUi(window.Close);
    }

    [Fact]
    public async Task TheLinkButtonAndBadgeAreCollapsedWithoutALinkOrBadge()
    {
        var (window, view) = await Show(new CollectionLoadoutDesignViewModel { Link = null, Badge = null });

        await OnUi(() =>
        {
            Named<StandardButton>(view, "LinkButton").IsEffectivelyVisible.Should().BeFalse();
            Named<StackPanel>(view, "BadgeStack").IsEffectivelyVisible.Should().BeFalse();
        });
        await OnUi(window.Close);
    }

    [Fact]
    public async Task TheLinkButtonAndBadgeShowTheirTextWhenTheSourceGivesThem()
    {
        var link = new CollectionLink("Open the thing", IconValues.NexusColor, ReactiveCommand.Create(() => { }), System.Reactive.Linq.Observable.Return(true));
        var (window, view) = await Show(new CollectionLoadoutDesignViewModel { Link = link, Badge = new CollectionBadge(IconValues.NexusColor, "Somewhere") });

        await EventuallyOnUi(() =>
        {
            Named<StandardButton>(view, "LinkButton").IsEffectivelyVisible.Should().BeTrue();
            ViewProbe.ShownTexts(view).Should().Contain("Open the thing").And.Contain("Somewhere");
        });
        await OnUi(window.Close);
    }

    [Fact]
    public async Task TheMoreFlyoutIsBuiltFromTheMenuItemsInTheirOrder()
    {
        var items = new IContextMenuItem[]
        {
            new ContextMenuItem { Header = "First item", Icon = IconValues.Copy, Command = ReactiveCommand.Create(() => { }) },
            new ContextMenuItem { Header = "Second item", Command = ReactiveCommand.Create(() => { }), Styling = ContextMenuItemStyling.Critical, ToolTip = "Careful" },
        };
        var (window, view) = await Show(new CollectionLoadoutDesignViewModel { MenuItems = items });

        await EventuallyOnUi(() =>
        {
            var flyout = view.GetVisualDescendants().OfType<StandardButton>().Select(button => button.Flyout).OfType<MenuFlyout>().Single();
            var menu = flyout.Items.OfType<MenuItem>().ToArray();
            menu.Select(item => item.Header).Should().Equal("First item", "Second item");
            menu[1].Classes.Should().Contain("Critical");
            ToolTip.GetTip(menu[1]).Should().Be("Careful");
        });
        await OnUi(window.Close);
    }

    [Theory]
    [InlineData(CollectionPageTab.Installed, true)]
    [InlineData(CollectionPageTab.Downloads, true)]
    [InlineData(CollectionPageTab.Downloads, false)]
    public async Task TheMoreButtonIsShownOnEveryTabAndWithoutAnInstalledGroup(CollectionPageTab tab, bool hasInstalledTab)
    {
        var items = new IContextMenuItem[] { new ContextMenuItem { Header = "Reinstall modlist", Command = ReactiveCommand.Create(() => { }) } };
        var (window, view) = await Show(new CollectionLoadoutDesignViewModel { MenuItems = items, SelectedTab = tab, HasInstalledTab = hasInstalledTab });

        await EventuallyOnUi(() =>
        {
            var more = Named<StandardButton>(view, "MoreButton");
            more.IsEffectivelyVisible.Should().BeTrue();
            ((MenuFlyout)more.Flyout!).Items.OfType<MenuItem>().Select(item => item.Header).Should().Equal("Reinstall modlist");
        });
        await OnUi(window.Close);
    }

    [Fact]
    public async Task WithoutAnInstalledGroupTheInstalledTabIsHidden()
    {
        var (window, view) = await Show(new CollectionLoadoutDesignViewModel { HasInstalledTab = false, SelectedTab = CollectionPageTab.Downloads });

        await OnUi(() => Named<TabItem>(view, "InstalledTab").IsVisible.Should().BeFalse());
        await OnUi(window.Close);
    }
}
