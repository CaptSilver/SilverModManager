using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages.Wabbajack;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>Renders the real modlist page headlessly and reads what a person would see.</summary>
public class WabbajackModlistViewTests(IServiceProvider provider) : AUiTest(provider)
{
    private const string Checking = "Checking the list against your library and game files. This can take a while for a big list.";

    private static readonly string[] TableRows = ["ArchiveRows", "GameFileRows", "SkippedRows"];

    // Long enough to wrap past three lines at the test window's width, so the page has to collapse it.
    private static readonly string TwoParagraphs = string.Join("\n\n", Enumerable.Repeat("A big Skyrim VR overhaul. Read the readme before installing; it needs a lot of room and a fast machine, and the first install takes an afternoon.", 5));

    /// <param name="readyText">A text that shows once the page has bound; a superseded page has no Install button, so it passes another.</param>
    private async Task<(Window Window, WabbajackModlistView View)> Show(WabbajackModlistDesignViewModel viewModel, double width = 1200, string readyText = "Install collection")
    {
        var (window, view) = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var view = new WabbajackModlistView { DataContext = viewModel };
            var window = Track(new Window { Width = width, Height = 900, Content = view });
            ShowRendered(window);
            return (window, view);
        });
        await EventuallyOnUi(() =>
        {
            Render(window);
            ShownTexts(view).Should().Contain(readyText, "the page has to bind before it is read");
        });
        return (window, view);
    }

    // A hidden control is still in the tree, so what the user can read is narrower than what is there.
    private static string[] ShownTexts(Control root, bool includeTables = false)
        => root.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text))
            .Where(t => includeTables || !t.GetVisualAncestors().OfType<Control>().Any(a => a.Name is { } n && TableRows.Contains(n)))
            .Select(t => t.Text!)
            .ToArray();

    private static T Named<T>(Control root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static WabbajackModlistDesignViewModel Colliding() => new()
    {
        IsPlanning = true,
        CanDownload = false,
        CheckText = "Hashing game files: 3.2 of 15.1 GB (Data/Skyrim - Textures1.bsa)",
        CheckFraction = 0.2,
        InstallBlockedReason = Checking,
        DownloadBlockedReason = Checking,
        OpenNextFiveBlockedReason = Checking,
        Description = TwoParagraphs,
    };

    [Fact]
    public async Task WhileTheListIsChecked_NoSentenceAppearsTwice()
    {
        var (window, view) = await Show(Colliding());

        await OnUi(() =>
        {
            var viewModel = (WabbajackModlistDesignViewModel)view.DataContext!;
            var header = viewModel.RunningStatus.Text;
            header.Should().StartWith("Hashing game files", "the collection page header draws the running check");

            // The header sits above this view on the collection page, so its line counts as part of the page.
            var texts = ShownTexts(view).Append(header).ToArray();
            texts.Should().OnlyHaveUniqueItems();
            texts.Should().NotContain(Checking, "the check is said once, by the header");
            ShownTexts(view).Should().NotContain(t => t.StartsWith("Hashing game files"), "the tab no longer draws the running step");
        });
        await OnUi(window.Close);
    }

    [Fact]
    public async Task ASupersededList_HidesInstall_WhereAnOrdinaryOneShowsIt()
    {
        var (superseded, view) = await Show(new WabbajackModlistDesignViewModel { SupersededBy = "2.0" }, readyText: "Open next five");
        var (ordinary, otherView) = await Show(new WabbajackModlistDesignViewModel());

        await EventuallyOnUi(() =>
        {
            Named<StandardButton>(view, "InstallButton").IsVisible.Should().BeFalse("the newer version is updated instead of installing a second copy");
            Named<StandardButton>(otherView, "InstallButton").IsVisible.Should().BeTrue();
        });
        await OnUi(superseded.Close);
        await OnUi(ordinary.Close);
    }

    [Fact]
    public async Task AReleaseOfAListAnotherReleaseHoldsHere_HidesInstall()
    {
        var (held, view) = await Show(new WabbajackModlistDesignViewModel { OtherVersion = "3.2" }, readyText: "Open next five");

        await EventuallyOnUi(() =>
            Named<StandardButton>(view, "InstallButton").IsVisible.Should().BeFalse("installing it would put a second copy of the list beside version 3.2"));
        await OnUi(held.Close);
    }

    [Fact]
    public async Task DownloadAndInstall_SitInTheToolbar_AndKeepTheirOwnReasonAsTooltip()
    {
        var (window, view) = await Show(Colliding());

        // A command's can-execute reaches the button on a later dispatcher turn.
        await EventuallyOnUi(() =>
        {
            foreach (var name in new[] { "DownloadButton", "InstallButton", "OpenNextFiveButton" })
            {
                var button = Named<StandardButton>(view, name);
                button.FindAncestorOfType<Statusbar>().Should().NotBeNull($"{name} belongs in the toolbar");
                ToolTip.GetTip(button).Should().Be(Checking);
                button.IsEffectivelyEnabled.Should().BeFalse($"{name} cannot run while the list is being checked");
            }
            Named<ComboBox>(view, "LoadoutBox").FindAncestorOfType<Statusbar>().Should().NotBeNull("the loadout picker sits beside Install");
            Named<CheckBox>(view, "SkipBox").FindAncestorOfType<Statusbar>().Should().NotBeNull("the skip toggle sits beside Install");
        });
        await OnUi(window.Close);
    }

    [Fact]
    public async Task WhenInstallIsBlockedByMissingArchives_TheReasonAndTheDropHintAppearOnce()
    {
        var (window, view) = await Show(new WabbajackModlistDesignViewModel
        {
            InstallBlockedReason = "3 of 312 archives are not in your library yet. Get what is missing.",
            DownloadBlockedReason = "",
        });

        await OnUi(() =>
        {
            var texts = ShownTexts(view);
            texts.Should().ContainSingle(t => t.Contains("not in your library yet"));
            texts.Should().ContainSingle(t => t.StartsWith("Drop downloaded archives"));
            texts.Should().OnlyHaveUniqueItems();
        });
        await OnUi(window.Close);
    }

    [Fact]
    public async Task APinnedLoadoutHidesThePicker()
    {
        var (window, view) = await Show(new WabbajackModlistDesignViewModel { IsLoadoutPinned = true });
        await EventuallyOnUi(() =>
        {
            Named<ComboBox>(view, "LoadoutBox").IsEffectivelyVisible.Should().BeFalse("the loadout is fixed by the collection page");
            Named<TextBlock>(view, "IntoLabel").IsEffectivelyVisible.Should().BeFalse();
        });
        await OnUi(window.Close);

        var (open, openView) = await Show(new WabbajackModlistDesignViewModel());
        await EventuallyOnUi(() =>
        {
            Named<ComboBox>(openView, "LoadoutBox").IsEffectivelyVisible.Should().BeTrue("a page opened without a loadout still picks one");
            Named<TextBlock>(openView, "IntoLabel").IsEffectivelyVisible.Should().BeTrue();
        });
        await OnUi(open.Close);
    }
}
