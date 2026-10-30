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

    private async Task<(Window Window, WabbajackModlistView View)> Show(WabbajackModlistDesignViewModel viewModel, double width = 1200)
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
            ShownTexts(view).Should().Contain("Install", "the page has to bind before it is read");
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
            var texts = ShownTexts(view);
            texts.Should().OnlyHaveUniqueItems();
            texts.Should().NotContain(Checking, "the check has its own line with a bar");
            texts.Should().ContainSingle(t => t.StartsWith("Hashing game files"));
        });
        await OnUi(window.Close);
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
    public async Task ALongDescription_IsCollapsedUntilShowMoreIsPressed()
    {
        var (window, view) = await Show(Colliding());

        // The toggle is decided from the laid-out text, which exists a turn after the page binds.
        await EventuallyOnUi(() =>
        {
            Named<TextBlock>(view, "DescriptionText").MaxLines.Should().Be(3);
            ShownTexts(view).Should().Contain("Show more");
            Click_AlreadyOnUi(Named<StandardButton>(view, "DescriptionToggle"));
            Render(window);
            Named<TextBlock>(view, "DescriptionText").MaxLines.Should().Be(0);
            ShownTexts(view).Should().Contain("Show less");
        });
        await OnUi(window.Close);
    }

    [Fact]
    public async Task AShortDescription_HasNoToggle_AndNoDescriptionHidesTheBlock()
    {
        var (window, view) = await Show(new WabbajackModlistDesignViewModel { Description = "A small list." });
        await OnUi(() =>
        {
            Named<StackPanel>(view, "DescriptionPanel").IsEffectivelyVisible.Should().BeTrue();
            Named<StandardButton>(view, "DescriptionToggle").IsEffectivelyVisible.Should().BeFalse();
        });
        await OnUi(window.Close);

        var (bare, bareView) = await Show(new WabbajackModlistDesignViewModel { Description = "" });
        await OnUi(() => Named<StackPanel>(bareView, "DescriptionPanel").IsEffectivelyVisible.Should().BeFalse());
        await OnUi(bare.Close);
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
    public async Task ALongSingleParagraph_ThatWrapsPastThreeLinesInANarrowWindow_GetsShowMore()
    {
        var text = "A big Skyrim VR overhaul with new textures, combat, quests and a lot of extra scripts that need room and a fast machine to run. " +
                   "Read the readme first, then give the first install an afternoon, and keep the game files on a fast drive.";
        text.Length.Should().BeLessThan(240, "a character count alone would call this short");
        var (window, view) = await Show(new WabbajackModlistDesignViewModel { Description = text }, width: 420);

        await EventuallyOnUi(() => ShownTexts(view).Should().Contain("Show more"));
        await OnUi(window.Close);
    }

    [Fact]
    public async Task ADescriptionThatFitsInThreeLines_HasNoToggle_EvenWithABlankLine()
    {
        var (window, view) = await Show(new WabbajackModlistDesignViewModel { Description = "One short line.\n\nAnother short line." });

        await OnUi(() => Named<StandardButton>(view, "DescriptionToggle").IsEffectivelyVisible.Should().BeFalse());
        await OnUi(window.Close);
    }

    [Fact]
    public async Task WithoutABanner_TheImageSlotIsGone_SoTheTitleAlignsWithTheRest()
    {
        var (window, view) = await Show(new WabbajackModlistDesignViewModel());

        await OnUi(() => Named<Border>(view, "CollectionImageBorder").IsEffectivelyVisible.Should().BeFalse());
        await OnUi(window.Close);
    }
}
