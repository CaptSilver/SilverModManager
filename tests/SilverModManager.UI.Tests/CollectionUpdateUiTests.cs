using Avalonia.Controls;
using Avalonia.Threading;
using DynamicData;
using DynamicData.Kernel;
using FluentAssertions;
using NSubstitute;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.App.UI.Pages.CollectionDownload.Dialogs.DroppedMods;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.Collections;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.UI.Sdk.Dialog;
using Observable = System.Reactive.Linq.Observable;

namespace SilverModManager.UI.Tests;

public class CollectionUpdateUiTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly DroppedMod[] Dropped = [new(LoadoutItemGroupId.From(10), "Old mod"), new(LoadoutItemGroupId.From(11), "Other old mod")];

    private static StandardDialogResult Pressed(string id) => new() { ButtonId = ButtonDefinitionId.From(id) };

    private static IWindowManager Answering(params StandardDialogResult[] answers)
    {
        var windows = Substitute.For<IWindowManager>();
        windows.ShowDialog(Arg.Any<IDialog>(), DialogWindowType.Modal).Returns(answers[0], answers.Skip(1).ToArray());
        return windows;
    }

    [Fact]
    public void TheQuestionNamesTheCountTheCollectionAndTheRevision()
    {
        var dialog = (Dialog)DroppedModsPrompt.Question("Big Collection", 7, 2);

        dialog.ViewModel.WindowTitle.Should().Be("Remove mods from the old revision?");
        dialog.ViewModel.ButtonDefinitions.Select(b => b.Text).Should().Equal("Keep all", "Review mods", "Remove all");
        var text = dialog.ViewModel.ContentViewModel.Should().BeOfType<DialogStandardContentViewModel>().Subject;
        text.Text.Should().Be("2 mods installed by Big Collection aren't in revision 7. Removing them avoids conflicts. Kept mods move to My Mods and are managed as your own.");
    }

    [Fact]
    public async Task RemoveAllReturnsEveryDroppedMod()
    {
        var removed = await DroppedModsPrompt.Ask(Answering(Pressed("remove-all")), "C", 2, Dropped);

        removed.Should().Equal(Dropped);
    }

    [Theory]
    [InlineData("keep-all")]
    [InlineData("")]
    public async Task KeepAllOrClosingTheDialogRemovesNothing(string pressed)
    {
        var answer = pressed.Length == 0 ? new StandardDialogResult() : Pressed(pressed);

        var removed = await DroppedModsPrompt.Ask(Answering(answer), "C", 2, Dropped);

        removed.Should().BeEmpty();
    }

    [Fact]
    public async Task ReviewStartsWithEveryModTickedAndRemovesTheTickedOnes()
    {
        var removed = await DroppedModsPrompt.Ask(Answering(Pressed("review"), Pressed("remove-selected")), "C", 2, Dropped);

        removed.Should().Equal(Dropped);
    }

    [Fact]
    public async Task ReviewThenKeepAllRemovesNothing()
    {
        var removed = await DroppedModsPrompt.Ask(Answering(Pressed("review"), Pressed("keep-all")), "C", 2, Dropped);

        removed.Should().BeEmpty();
    }

    [Fact]
    public void TheChecklistReturnsOnlyTheModsThatStayTicked()
    {
        var (dialog, checklist) = DroppedModsPrompt.Review(Dropped);
        checklist.Mods.Should().OnlyContain(choice => choice.Remove);

        checklist.Mods[0].Remove = false;

        checklist.Selected().Should().Equal(Dropped[1]);
        ((Dialog)dialog).ViewModel.ButtonDefinitions.Select(b => b.Text).Should().Equal("Keep all", "Remove selected");
    }

    [Fact]
    public async Task NoQuestionIsAskedWhenNothingWasDropped()
    {
        var windows = Substitute.For<IWindowManager>();

        var removed = await DroppedModsPrompt.Ask(windows, "C", 2, []);

        removed.Should().BeEmpty();
        _ = windows.DidNotReceive().ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>());
    }

    [Fact]
    public void TheTabTitleCarriesTheRevisionOnlyWhenANewerOneExists()
    {
        var rev = RevisionNumber.From(2);

        CollectionDownloadViewModel.TabTitleOf("Big Collection", rev, Optional.Some(RevisionNumber.From(5))).Should().Be("Big Collection · rev 2");
        CollectionDownloadViewModel.TabTitleOf("Big Collection", rev, Optional<RevisionNumber>.None).Should().Be("Big Collection");
    }

    private sealed class NoRowsAdapter(IServiceProvider provider) : CollectionDownloadTreeDataGridAdapter(provider, default, default)
    {
        protected override IObservable<IChangeSet<CompositeItemModel<EntityId>, EntityId>> GetRootsObservable(bool viewHierarchical) =>
            Observable.Empty<IChangeSet<CompositeItemModel<EntityId>, EntityId>>();
    }

    private async Task<(CollectionDownloadView View, CollectionDownloadDesignViewModel ViewModel)> ShowDownloadView()
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var viewModel = new CollectionDownloadDesignViewModel(isEmbedded: false, new NoRowsAdapter(Provider));
            var view = new CollectionDownloadView { ViewModel = viewModel };
            Track(new Window { Width = 1000, Height = 800, Content = view }).Show();
            return (view, viewModel);
        });
    }

    private static T Find<T>(Control view, string name) where T : Control =>
        view.FindControl<T>(name) ?? throw new InvalidOperationException($"No control `{name}`");

    [Fact]
    public async Task AnOlderRevisionsPageSaysSoAndNamesTheNewestRevision()
    {
        var (view, viewModel) = await ShowDownloadView();
        await Dispatcher.UIThread.InvokeAsync(() => Find<TextBlock>(view, "OlderRevisionBanner").IsVisible.Should().BeFalse("the design revision is the newest"));

        await Dispatcher.UIThread.InvokeAsync(() => viewModel.NewestRevisionNumber.Value = Optional.Some(RevisionNumber.From(9)));

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var banner = Find<TextBlock>(view, "OlderRevisionBanner");
            banner.IsVisible.Should().BeTrue();
            banner.Text.Should().Be("Revision 6 is an older revision of this collection. The newest is revision 9.");
        });
    }

    [Fact]
    public async Task ThePrimaryActionReadsUpdateWhenAnOlderRevisionIsInstalled()
    {
        var (view, viewModel) = await ShowDownloadView();
        await Dispatcher.UIThread.InvokeAsync(() => Find<StandardButton>(view, "ButtonInstallRequiredItems").Text.Should().Be(Language.CollectionDownloadViewModel_InstallCollection));

        await Dispatcher.UIThread.InvokeAsync(() => viewModel.WillUpdateInstalledRevision.Value = true);

        await Dispatcher.UIThread.InvokeAsync(() => Find<StandardButton>(view, "ButtonInstallRequiredItems").Text.Should().Be("Update collection"));
    }

    [Fact]
    public void TheQuestionReadsRightForASingleMod()
    {
        var dialog = (Dialog)DroppedModsPrompt.Question("Big Collection", 7, 1);

        dialog.ViewModel.ContentViewModel.Should().BeOfType<DialogStandardContentViewModel>().Subject.Text
            .Should().Be("1 mod installed by Big Collection isn't in revision 7. Removing it avoids conflicts. A kept mod moves to My Mods and is managed as your own.");
    }

    private static CollectionUpdateResult ResultWith(int failed, int oldVersionsKept, string[]? skippedRules = null, Exception? error = null)
    {
        var failures = Enumerable.Range(0, failed).Select(i => new InstallCollectionJob.FailedItem(default, $"Mod {i}", new InvalidOperationException("no"))).ToArray();
        return new CollectionUpdateResult(default, [], failures, oldVersionsKept, skippedRules ?? [], error);
    }

    [Fact]
    public void TheToastSaysHowManyModsFailedAndThatOldVersionsWereKept()
    {
        CollectionUpdateFlow.ResultText(ResultWith(0, 0)).Should().Be("Collection updated");
        CollectionUpdateFlow.ResultText(ResultWith(1, 0)).Should().Be("Collection updated, but 1 mod failed to install.");
        CollectionUpdateFlow.ResultText(ResultWith(3, 2)).Should().Be("Collection updated, but 3 mods failed to install. Where there was an older version of a failed mod, it was kept in My Mods.");
    }

    [Fact]
    public void TheToastCountsTheRulesThatCouldNotBeCarriedOver()
    {
        CollectionUpdateFlow.ResultText(ResultWith(0, 0, skippedRules: ["a wins over b", "c wins over d"]))
            .Should().Be("Collection updated. 2 of your rules couldn't be carried over to the new versions because they contradict other rules.");
    }

    [Fact]
    public void AnInstallThatStoppedEarlyIsReportedWithItsReason()
    {
        CollectionUpdateFlow.ResultText(ResultWith(0, 0, error: new InvalidOperationException("rules clash")), revision: 4)
            .Should().Be("Update to revision 4 stopped early: rules clash");
    }

    [Fact]
    public async Task ThePrimaryActionExplainsWhyItIsOffWhenANewerRevisionIsInstalled()
    {
        var (view, viewModel) = await ShowDownloadView();
        await Dispatcher.UIThread.InvokeAsync(() => ToolTip.GetTip(Find<StandardButton>(view, "ButtonInstallRequiredItems")).Should().BeNull());

        await Dispatcher.UIThread.InvokeAsync(() => viewModel.IsOlderThanInstalledRevision.Value = true);

        await Dispatcher.UIThread.InvokeAsync(() => ToolTip.GetTip(Find<StandardButton>(view, "ButtonInstallRequiredItems"))
            .Should().Be("A newer revision of this collection is installed, so this one can't replace it."));
    }
}
