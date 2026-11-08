using Avalonia.Controls;
using Avalonia.Threading;
using System.Reflection;
using DynamicData;
using DynamicData.Kernel;
using FluentAssertions;
using NSubstitute;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.App.UI.Pages.CollectionDownload.Dialogs.DroppedMods;
using SilverModManager.App.UI.Pages.Collections.Update;
using NexusMods.Paths;
using SilverModManager.UI.Sdk;
using SilverModManager.Sdk.Jobs;
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

    /// <summary>Records what one press of Update did, in order.</summary>
    private sealed class UpdatePress(bool downloaded, bool canDownload = true, bool downloadCompletes = true)
    {
        public List<string> Steps { get; } = [];
        private bool _downloaded = downloaded;

        public Task<CollectionUpdateFlow.UpdateStart> Run() => CollectionUpdateFlow.DownloadThenUpdate(
            isDownloaded: () => _downloaded,
            ensureCanDownload: _ => { Steps.Add("check login"); return Task.FromResult(canDownload); },
            download: _ => { Steps.Add("download"); _downloaded = downloadCompletes; return Task.CompletedTask; },
            update: _ => { Steps.Add("update"); return Task.CompletedTask; },
            CancellationToken.None);
    }

    [Fact]
    public async Task UpdateDownloadsTheMissingModsAndUpdatesInOnePress()
    {
        var press = new UpdatePress(downloaded: false);

        (await press.Run()).Should().Be(CollectionUpdateFlow.UpdateStart.Updated);
        press.Steps.Should().Equal("check login", "download", "update");
    }

    [Fact]
    public async Task UpdateSkipsTheDownloadWhenEverythingIsDownloaded()
    {
        var press = new UpdatePress(downloaded: true);

        (await press.Run()).Should().Be(CollectionUpdateFlow.UpdateStart.Updated);
        press.Steps.Should().Equal("update");
    }

    [Fact]
    public async Task UpdateStopsWhenTheModsCantBeDownloaded()
    {
        var press = new UpdatePress(downloaded: false, canDownload: false);

        (await press.Run()).Should().Be(CollectionUpdateFlow.UpdateStart.CantDownload);
        press.Steps.Should().Equal("check login");
    }

    [Fact]
    public async Task UpdateDoesntRunOnAPartialDownload()
    {
        var press = new UpdatePress(downloaded: false, downloadCompletes: false);

        (await press.Run()).Should().Be(CollectionUpdateFlow.UpdateStart.NotAllDownloaded);
        press.Steps.Should().Equal("check login", "download");
    }

    [Fact]
    public void TheInstalledStatusReadsReadyWithNothingToReview()
    {
        CollectionDownloadViewModel.InstalledStatusText(0).Should().Be(Language.CollectionDownloadViewModel_CollectionDownloadViewModel_Ready_to_play___All_required_mods_installed);
    }

    [Fact]
    public void TheInstalledStatusSaysOneModNeedsReview()
    {
        CollectionDownloadViewModel.InstalledStatusText(1).Should().Be("Installed. 1 mod needs review");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    public void TheInstalledStatusCountsTheModsToReview(int pending)
    {
        CollectionDownloadViewModel.InstalledStatusText(pending).Should().Be($"Installed. {pending} mods need review");
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

    private static CollectionUpdateResult ResultWith(int failed, int oldVersionsKept, string[]? skippedRules = null, Exception? error = null, int pending = 0)
    {
        var pendingMods = Enumerable.Range(0, pending).Select(i => new InstallCollectionJob.PendingItem(default, $"Pending {i}", ReviewKind.NoInstaller, "no installer")).ToArray();
        var failures = Enumerable.Range(0, failed).Select(i => new InstallCollectionJob.FailedItem(default, $"Mod {i}", new InvalidOperationException("no"))).ToArray();
        return new CollectionUpdateResult(default, [], failures, pendingMods, oldVersionsKept, skippedRules ?? [], error);
    }

    [Fact]
    public void TheToastSaysHowManyModsFailedAndThatOldVersionsWereKept()
    {
        CollectionUpdateFlow.ResultText(ResultWith(0, 0)).Should().Be("Collection updated");
        CollectionUpdateFlow.ResultText(ResultWith(1, 0)).Should().Be("Collection updated, but 1 mod failed to install.");
        CollectionUpdateFlow.ResultText(ResultWith(3, 2)).Should().Be("Collection updated, but 3 mods failed to install. An older version was kept in My Mods for mods that failed or still need review.");
    }

    [Fact]
    public void ResultText_SaysHowManyModsNeedReview()
    {
        CollectionUpdateFlow.ResultText(ResultWith(0, 0, pending: 1)).Should().Be("Collection updated. 1 mod needs review.");
        CollectionUpdateFlow.ResultText(ResultWith(0, 0, pending: 3)).Should().Be("Collection updated. 3 mods need review.");
        CollectionUpdateFlow.ResultText(ResultWith(1, 0, pending: 2)).Should().Be("Collection updated, but 1 mod failed to install. 2 mods need review.");
    }

    [Fact]
    public void ResultText_JoinsThePendingCountAndTheKeptOldVersionSentence()
    {
        CollectionUpdateFlow.ResultText(ResultWith(0, 1, pending: 1))
            .Should().Be("Collection updated. 1 mod needs review. An older version was kept in My Mods for mods that failed or still need review.");
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

    private static CollectionUpdateReview ReviewOf(bool needsBrowser = false, bool writesRestorePoint = false, string[]? added = null) => new(
        Downloads: [new ReviewArchive("a.7z", Size.From(1024), "Nexus", needsBrowser)],
        DownloadSize: Size.From(1024), NewBytesUpperBound: null, DiskNote: "Room for the largest 4 archives while they extract",
        Added: added ?? ["New mod"], Changed: [], Renamed: [new ReviewRename("Old name", "New name")], Dropped: [],
        ModOrderChanges: [], PluginChanges: [], UserEditsKept: [], GameFilesKept: [], Warnings: [],
        WritesRestorePoint: writesRestorePoint, DroppedAreRemovedNotAsked: false);

    private static CollectionUpdateOutcome OutcomeOf(
        Exception? error = null, Func<CancellationToken, Task>? undo = null, IReadOnlyList<DroppedMod>? toAsk = null, IReadOnlyList<string>? removed = null) =>
        new(Updated: error is null, Failed: 0, Pending: 0, DroppedToAsk: toAsk ?? [], Removed: removed ?? [], RestorePoint: null, Undo: undo, Error: error, ToastText: "Toast text");

    /// <summary>Records what the flow asked of an update, in order.</summary>
    private sealed class FakeUpdate(bool downloaded, CollectionUpdateOutcome? outcome = null) : ICollectionUpdate
    {
        public List<string> Calls { get; } = [];
        public bool? SkipMissing { get; private set; }
        public CollectionUpdateReview Review_ { get; init; } = ReviewOf();
        public CollectionUpdateDownload DownloadResult { get; init; } = CollectionUpdateDownload.Downloaded;
        public bool ReviewWaitsForCancel { get; init; }
        public string? ReviewReports { get; init; }
        public Exception? ApplyThrows { get; init; }

        public string Name => "Big List";
        public string CurrentVersionText => "1.0";
        public string NewVersionText => "2.0";
        public string? Note => null;

        public async Task<CollectionUpdateReview> Review(IProgress<string>? progress, CancellationToken ct)
        {
            Calls.Add("review");
            if (ReviewReports is not null) progress?.Report(ReviewReports);
            if (ReviewWaitsForCancel) await Task.Delay(Timeout.Infinite, ct);
            return Review_;
        }
        public Task<bool> IsDownloaded(CancellationToken ct) { Calls.Add("isDownloaded"); return Task.FromResult(downloaded); }
        public Task<CollectionUpdateDownload> Download(CancellationToken ct) { Calls.Add("download"); return Task.FromResult(DownloadResult); }
        public Task<CollectionUpdateOutcome> Apply(bool skipMissing, CancellationToken ct)
        {
            Calls.Add("apply");
            SkipMissing = skipMissing;
            if (ApplyThrows is not null) throw ApplyThrows;
            return Task.FromResult(outcome ?? OutcomeOf());
        }
    }

    private static bool IsProgress(IDialog dialog) => ((Dialog)dialog).ViewModel.ContentViewModel is CollectionUpdateProgressViewModel;

    /// <summary>
    /// A window manager that keeps the progress dialog open until the flow closes it, as a real one does, and answers
    /// every other dialog in turn with <paramref name="answers"/> (the last repeats).
    /// </summary>
    private static IWindowManager Windows(params StandardDialogResult[] answers)
    {
        var windows = Substitute.For<IWindowManager>();
        var next = 0;
        windows.ShowDialog(Arg.Any<IDialog>(), DialogWindowType.Modal).Returns(call =>
            IsProgress(call.Arg<IDialog>())
                ? new TaskCompletionSource<StandardDialogResult>().Task
                : Task.FromResult(answers[Math.Min(next++, answers.Length - 1)]));
        return windows;
    }

    private static IWindowManager ReviewAnsweredWith(ButtonDefinitionId button) => Windows(new StandardDialogResult { ButtonId = button });

    private static IDialog[] DialogsShown(IWindowManager windows) => windows.ReceivedCalls()
        .Where(call => call.GetMethodInfo().Name == nameof(IWindowManager.ShowDialog))
        .Select(call => (IDialog)call.GetArguments()[0]!)
        .Where(dialog => !IsProgress(dialog))
        .ToArray();

    [Fact]
    public async Task CancellingTheReviewCallsNothingAfterReview()
    {
        var update = new FakeUpdate(downloaded: false);

        await CollectionUpdateFlow.Run(Provider, ReviewAnsweredWith(ButtonDefinitionId.Cancel), Substitute.For<IWindowNotificationService>(), update, CancellationToken.None);

        update.Calls.Should().NotContain(["download", "apply"]);
        update.Calls.Should().Contain("review");
    }

    [Fact]
    public async Task AcceptingAnUndownloadedReviewDownloadsThenApplies()
    {
        var update = new FakeUpdate(downloaded: false);
        var toasts = Substitute.For<IWindowNotificationService>();

        await CollectionUpdateFlow.Run(Provider, ReviewAnsweredWith(ButtonDefinitionId.Accept), toasts, update, CancellationToken.None);

        update.Calls.Where(call => call is "download" or "apply").Should().Equal("download", "apply");
        toasts.Received(1).ShowToast("Toast text", ToastNotificationVariant.Success, Arg.Any<TimeSpan?>(), Arg.Any<DialogButtonDefinition[]?>(), Arg.Any<Action<ButtonDefinitionId>?>());
    }

    [Fact]
    public async Task AnAlreadyDownloadedReviewAppliesWithoutDownloading()
    {
        var update = new FakeUpdate(downloaded: true);

        await CollectionUpdateFlow.Run(Provider, ReviewAnsweredWith(ButtonDefinitionId.Accept), Substitute.For<IWindowNotificationService>(), update, CancellationToken.None);

        update.Calls.Should().NotContain("download");
        update.Calls.Should().Contain("apply");
    }

    [Fact]
    public async Task ADownloadThatFallsShortAppliesNothingAndSaysSo()
    {
        var update = new FakeUpdate(downloaded: false) { DownloadResult = CollectionUpdateDownload.NotAllDownloaded };
        var toasts = Substitute.For<IWindowNotificationService>();

        await CollectionUpdateFlow.Run(Provider, ReviewAnsweredWith(ButtonDefinitionId.Accept), toasts, update, CancellationToken.None);

        update.Calls.Should().NotContain("apply");
        toasts.Received(1).ShowToast(Arg.Is<string>(m => m.Contains("2.0")), ToastNotificationVariant.Failure, Arg.Any<TimeSpan?>(), Arg.Any<DialogButtonDefinition[]?>(), Arg.Any<Action<ButtonDefinitionId>?>());
    }

    [Fact]
    public async Task ADownloadThatCouldNotStartAppliesNothing()
    {
        var update = new FakeUpdate(downloaded: false) { DownloadResult = CollectionUpdateDownload.CantDownload };

        await CollectionUpdateFlow.Run(Provider, ReviewAnsweredWith(ButtonDefinitionId.Accept), Substitute.For<IWindowNotificationService>(), update, CancellationToken.None);

        update.Calls.Should().NotContain("apply");
    }

    [Fact]
    public async Task AFailedApplyWithARestorePointOffersUndoInADialogAndConfirmsTheUndo()
    {
        var undone = 0;
        var update = new FakeUpdate(downloaded: true, OutcomeOf(new InvalidOperationException("disk full"), undo: _ => { undone++; return Task.CompletedTask; }));
        var toasts = Substitute.For<IWindowNotificationService>();
        var windows = Windows(Pressed("accept"), Pressed("undo-update"));

        await CollectionUpdateFlow.Run(Provider, windows, toasts, update, CancellationToken.None);

        var offer = (Dialog)DialogsShown(windows).Last();
        offer.ViewModel.ButtonDefinitions.Select(b => b.Text).Should().Equal("Close", "Undo update");
        undone.Should().Be(1);
        toasts.Received(1).ShowToast("Update undone: Big List is back at 1.0.", ToastNotificationVariant.Success, Arg.Any<TimeSpan?>(), Arg.Any<DialogButtonDefinition[]?>(), Arg.Any<Action<ButtonDefinitionId>?>());
    }

    [Fact]
    public async Task ClosingTheUndoOfferLeavesTheUpdateAlone()
    {
        var undone = 0;
        var update = new FakeUpdate(downloaded: true, OutcomeOf(new InvalidOperationException("disk full"), undo: _ => { undone++; return Task.CompletedTask; }));
        var toasts = Substitute.For<IWindowNotificationService>();

        await CollectionUpdateFlow.Run(Provider, Windows(Pressed("accept"), Pressed("close")), toasts, update, CancellationToken.None);

        undone.Should().Be(0);
        toasts.DidNotReceive().ShowToast(Arg.Is<string>(m => m.StartsWith("Update undone")), Arg.Any<ToastNotificationVariant>(), Arg.Any<TimeSpan?>(), Arg.Any<DialogButtonDefinition[]?>(), Arg.Any<Action<ButtonDefinitionId>?>());
    }

    [Fact]
    public async Task AnUndoThatFailsSaysSoInsteadOfClaimingSuccess()
    {
        var update = new FakeUpdate(downloaded: true, OutcomeOf(new InvalidOperationException("disk full"), undo: _ => throw new InvalidOperationException("restore broke")));
        var toasts = Substitute.For<IWindowNotificationService>();

        await CollectionUpdateFlow.Run(Provider, Windows(Pressed("accept"), Pressed("undo-update")), toasts, update, CancellationToken.None);

        toasts.Received(1).ShowToast("restore broke", ToastNotificationVariant.Failure, Arg.Any<TimeSpan?>(), Arg.Any<DialogButtonDefinition[]?>(), Arg.Any<Action<ButtonDefinitionId>?>());
        toasts.DidNotReceive().ShowToast(Arg.Is<string>(m => m.StartsWith("Update undone")), Arg.Any<ToastNotificationVariant>(), Arg.Any<TimeSpan?>(), Arg.Any<DialogButtonDefinition[]?>(), Arg.Any<Action<ButtonDefinitionId>?>());
    }

    [Fact]
    public async Task AFailedApplyWithoutARestorePointOffersNoUndo()
    {
        var update = new FakeUpdate(downloaded: true, OutcomeOf(new InvalidOperationException("disk full"), undo: null));
        var toasts = Substitute.For<IWindowNotificationService>();

        await CollectionUpdateFlow.Run(Provider, ReviewAnsweredWith(ButtonDefinitionId.Accept), toasts, update, CancellationToken.None);

        toasts.Received(1).ShowToast("Toast text", ToastNotificationVariant.Failure, Arg.Any<TimeSpan?>(), null, null);
    }

    [Fact]
    public async Task NothingIsAskedAboutDroppedModsWhenTheUpdateRemovedThem()
    {
        var update = new FakeUpdate(downloaded: true, OutcomeOf(removed: ["Gone mod"]));
        var windows = ReviewAnsweredWith(ButtonDefinitionId.Accept);

        await CollectionUpdateFlow.Run(Provider, windows, Substitute.For<IWindowNotificationService>(), update, CancellationToken.None);

        DialogsShown(windows).Should().HaveCount(1);
    }

    [Fact]
    public async Task DroppedModsTheUpdateLeftToTheUserAreAskedAbout()
    {
        var update = new FakeUpdate(downloaded: true, OutcomeOf(toAsk: Dropped));
        var windows = Windows(Pressed("accept"), Pressed("keep-all"));

        await CollectionUpdateFlow.Run(Provider, windows, Substitute.For<IWindowNotificationService>(), update, CancellationToken.None);

        DialogsShown(windows).Should().HaveCount(2);
    }

    [Fact]
    public void TheReviewDialogNamesTheUpdateAndOffersDownloadWhenSomethingIsMissing()
    {
        var update = new FakeUpdate(downloaded: false);

        var (dialog, model) = CollectionUpdateReviewDialog.Create(update, ReviewOf(writesRestorePoint: true), isDownloaded: false);

        var view = ((Dialog)dialog).ViewModel;
        view.WindowTitle.Should().Be("Big List 1.0 → 2.0");
        view.ButtonDefinitions.Select(b => b.Text).Should().Equal("Cancel", "Download and update");
        model.RestorePointText.Should().Be("A restore point is written first; Undo is on the modlist page.");
        model.Sections.Select(s => s.Title).Should().Equal("Archives to download (1, 1 KB)", "Added (1)", "Renamed (1)");
    }

    [Fact]
    public void TheReviewDialogOffersUpdateWhenEverythingIsDownloaded()
    {
        var (dialog, model) = CollectionUpdateReviewDialog.Create(new FakeUpdate(downloaded: true), ReviewOf(), isDownloaded: true);

        ((Dialog)dialog).ViewModel.ButtonDefinitions.Select(b => b.Text).Should().Equal("Cancel", "Update");
        model.RestorePointText.Should().BeNull();
    }

    [Fact]
    public void TheSkipMissingBoxIsOfferedOnlyWhenAnArchiveNeedsTheBrowser()
    {
        CollectionUpdateReviewDialog.Create(new FakeUpdate(false), ReviewOf(needsBrowser: true), false).Model.CanSkipMissing.Should().BeTrue();
        CollectionUpdateReviewDialog.Create(new FakeUpdate(false), ReviewOf(needsBrowser: false), false).Model.CanSkipMissing.Should().BeFalse();
    }

    [Fact]
    public async Task TheSkipMissingChoiceReachesApply()
    {
        var update = new FakeUpdate(downloaded: true) { Review_ = ReviewOf(needsBrowser: true) };
        var windows = Substitute.For<IWindowManager>();
        windows.ShowDialog(Arg.Any<IDialog>(), DialogWindowType.Modal).Returns(call =>
        {
            if (IsProgress(call.Arg<IDialog>())) return new TaskCompletionSource<StandardDialogResult>().Task;
            ((CollectionUpdateReviewViewModel)((Dialog)call.Arg<IDialog>()).ViewModel.ContentViewModel).SkipMissing = true;
            return Task.FromResult(Pressed("accept"));
        });

        await CollectionUpdateFlow.Run(Provider, windows, Substitute.For<IWindowNotificationService>(), update, CancellationToken.None);

        update.SkipMissing.Should().BeTrue();
    }

    [Fact]
    public async Task CancellingDuringTheReviewCallsNothingAfterIt()
    {
        var update = new FakeUpdate(downloaded: false) { ReviewWaitsForCancel = true };
        var windows = Substitute.For<IWindowManager>();
        windows.ShowDialog(Arg.Any<IDialog>(), DialogWindowType.Modal).Returns(Task.FromResult(new StandardDialogResult { ButtonId = ButtonDefinitionId.Cancel }));
        var toasts = Substitute.For<IWindowNotificationService>();

        await CollectionUpdateFlow.Run(Provider, windows, toasts, update, CancellationToken.None);

        update.Calls.Should().Equal("review");
        _ = windows.Received(1).ShowDialog(Arg.Any<IDialog>(), DialogWindowType.Modal);
        toasts.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task TheProgressDialogShowsWhatTheReviewReportsAndClosesWhenItIsDone()
    {
        var update = new FakeUpdate(downloaded: true) { ReviewReports = "Hashing archive 3 of 9" };
        var shown = new List<CollectionUpdateProgressViewModel>();
        var windows = Substitute.For<IWindowManager>();
        windows.ShowDialog(Arg.Any<IDialog>(), DialogWindowType.Modal).Returns(call =>
        {
            if (!IsProgress(call.Arg<IDialog>())) return Task.FromResult(Pressed("cancel"));
            shown.Add((CollectionUpdateProgressViewModel)((Dialog)call.Arg<IDialog>()).ViewModel.ContentViewModel);
            return new TaskCompletionSource<StandardDialogResult>().Task;
        });

        await CollectionUpdateFlow.Run(Provider, windows, Substitute.For<IWindowNotificationService>(), update, CancellationToken.None);

        await Dispatcher.UIThread.InvokeAsync(() => shown.Should().ContainSingle().Which.Text.Should().Be("Hashing archive 3 of 9"));
    }

    [Fact]
    public void TheReviewDialogLeadsTheDiskNoteWithTheStoreGrowthBoundEvenWhenNothingDownloads()
    {
        var review = ReviewOf() with { Downloads = [], DownloadSize = Size.Zero, NewBytesUpperBound = Size.From(2048) };

        var (_, model) = CollectionUpdateReviewDialog.Create(new FakeUpdate(downloaded: true), review, isDownloaded: true);

        model.DiskNote.Should().Be("The file store grows by at most 2 KB; nothing is downloaded twice. Room for the largest 4 archives while they extract");
    }

    [Fact]
    public void TheReviewDialogShowsJustTheDiskNoteWhenThereIsNoGrowthBound()
    {
        var (_, model) = CollectionUpdateReviewDialog.Create(new FakeUpdate(downloaded: false), ReviewOf(), isDownloaded: false);

        model.DiskNote.Should().Be("Room for the largest 4 archives while they extract");
    }

    [Fact]
    public async Task TheGenericFlowNeverCallsAVersionARevision()
    {
        var failing = new FakeUpdate(downloaded: false) { DownloadResult = CollectionUpdateDownload.NotAllDownloaded };
        var toasts = Substitute.For<IWindowNotificationService>();
        await CollectionUpdateFlow.Run(Provider, ReviewAnsweredWith(ButtonDefinitionId.Accept), toasts, failing, CancellationToken.None);

        var throwing = new FakeUpdate(downloaded: true) { ApplyThrows = new InvalidOperationException("boom") };
        await CollectionUpdateFlow.Run(Provider, ReviewAnsweredWith(ButtonDefinitionId.Accept), toasts, throwing, CancellationToken.None);

        var messages = toasts.ReceivedCalls().Select(call => (string)call.GetArguments()[0]!).ToArray();
        messages.Should().HaveCount(2).And.OnlyContain(m => !m.Contains("revision", StringComparison.OrdinalIgnoreCase));
        messages[1].Should().Be("Couldn't update Big List to 2.0: boom");
    }

    [Fact]
    public void TheDroppedModsQuestionForAVersionStringDoesNotSayRevisionButTheNexusOneStill()
    {
        static string TextOf(IDialog dialog) => ((DialogStandardContentViewModel)((Dialog)dialog).ViewModel.ContentViewModel).Text;

        TextOf(DroppedModsPrompt.Question("Big List", "2.0", 2)).Should().Contain("aren't in version 2.0").And.NotContain("revision");
        TextOf(DroppedModsPrompt.Question("Big List", "2.0", 1)).Should().Contain("isn't in version 2.0").And.NotContain("revision");
        TextOf(DroppedModsPrompt.Question("C", 7, 2)).Should().Contain("aren't in revision 7");
    }
}
