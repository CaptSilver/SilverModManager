using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Files.Diff;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Controls.Trees;
using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.App.UI.Pages.Diff.ApplyDiff;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;
using ReactiveUI;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests;

/// <summary>
/// Files an Apply cannot write, because no copy of them is kept, are not pending work: the preview marks them and
/// does not count them as additions, the page offers to settle them in place, and an Apply that left some out says so.
/// </summary>
public class ApplyDiffUnwritableFilesTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly GamePath Missing = new(LocationId.Game, "archive/pc/mod/missing.archive");
    private static readonly GamePath Changed = new(LocationId.Game, "archive/pc/mod/changed.archive");

    private readonly List<IDisposable> _activations = [];

    public override async ValueTask DisposeAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => _activations.ForEach(a => a.Dispose()));
        await base.DisposeAsync();
    }

    private sealed class Harness
    {
        public required ApplyDiffViewModel Page { get; init; }
        public required Loadout.ReadOnly Loadout { get; init; }
        public required ISynchronizerService Sync { get; init; }
        public required IErrorPresenter Presenter { get; init; }
        public required IWindowNotificationService Toasts { get; init; }
        public required BehaviorSubject<LoadoutSynchronizerState> Status { get; init; }
        public required List<UnwritableFile> Unwritable { get; init; }
        public bool CanKeep { get; set; }
        public bool CanForget { get; set; }
        public DiffTreeViewModel Tree => (DiffTreeViewModel)Page.BodyViewModel!;
    }

    private static KeyValuePair<GamePath, DiskDiffEntry> Entry(GamePath path, FileChangeType change) =>
        new(path, new DiskDiffEntry { GamePath = path, Size = Size.From(1), Identity = FileIdentity.XxHash3(Hash.From(1)), ChangeType = change });

    private async Task<Harness> Open(string name, params UnwritableFile[] unwritable)
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, name);
        var status = new BehaviorSubject<LoadoutSynchronizerState>(LoadoutSynchronizerState.NeedsSync);
        var remaining = unwritable.ToList();
        var sync = Substitute.For<ISynchronizerService>();
        // What the page reads is what the synchronizer says is left, so settling a file removes its row and its entry.
        sync.GetApplyDiffTree(Arg.Any<LoadoutId>()).Returns(_ => FileDiffTree.Create(remaining.Select(f => Entry(f.Path, FileChangeType.Unrestorable)).ToArray()));
        sync.GetUnwritableFiles(Arg.Any<LoadoutId>()).Returns(_ => remaining.ToArray());
        sync.ResolveUnwritableFiles(Arg.Any<LoadoutId>(), Arg.Any<IReadOnlyCollection<GamePath>?>(), Arg.Any<UnwritableResolution>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var resolution = call.Arg<UnwritableResolution>();
                var fits = remaining.Where(f => (f.Reason == UnwritableReason.DifferentOnDisk) == (resolution == UnwritableResolution.KeepDiskFile)).ToList();
                remaining.RemoveAll(fits.Contains);
                return Task.FromResult(new UnwritableResolutionResult(fits.Select(f => f.Path).ToArray(), []));
            });
        sync.StatusForLoadout(Arg.Any<LoadoutId>()).Returns(Task.FromResult<IObservable<LoadoutSynchronizerState>>(status));
        var presenter = Substitute.For<IErrorPresenter>();
        presenter.Report(Arg.Any<Exception>(), Arg.Any<string>()).Returns(Task.CompletedTask);
        var toasts = Substitute.For<IWindowNotificationService>();
        var windows = Substitute.For<IWindowManager>();

        var services = new ServiceCollection()
            .AddSingleton(sync).AddSingleton(Connection).AddSingleton(presenter).AddSingleton(toasts)
            .AddSingleton(Substitute.For<IDiagnosticManager>()).AddLogging()
            .BuildServiceProvider();

        var page = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var vm = new ApplyDiffViewModel(windows, services);
            vm.Initialize(loadout.LoadoutId);
            return vm;
        });
        _activations.Add(await Dispatcher.UIThread.InvokeAsync(() => page.Activator.Activate()));
        var harness = new Harness { Page = page, Loadout = loadout, Sync = sync, Presenter = presenter, Toasts = toasts, Status = status, Unwritable = remaining };
        page.KeepDiskFilesCommand.CanExecute.Subscribe(can => harness.CanKeep = can);
        page.ForgetFilesCommand.CanExecute.Subscribe(can => harness.CanForget = can);
        await Eventually(() => page.BodyViewModel.Should().NotBeNull());
        return harness;
    }

    private static Task Run(ReactiveCommand<Unit, Unit> command) =>
        Dispatcher.UIThread.InvokeAsync(() => { command.Execute().Subscribe(); }).GetTask();

    [Fact]
    public async Task TheStatusBarDoesNotCountAFileNothingCanWriteAsAnAddition()
    {
        var h = await Open("Unwritable Status", new UnwritableFile(Missing, UnwritableReason.NotOnDisk));

        await Eventually(() => h.Tree.StatusBarStrings.Should().Contain(s => s.Contains("Adding 0 Modifying 0 Removing 0")));
        h.Tree.StatusBarStrings.Should().Contain(string.Format(Language.DiffTreeViewModel_StatusBar__Unrestorable, 1));
        h.Tree.StatusBarStrings.Should().NotContain(s => s.Contains("Adding 1"));
    }

    [Fact]
    public void ARowForAFileNothingCanWriteSaysSoAndIsNotStyledAsAnAddition()
    {
        SilverModManager.App.UI.Controls.Trees.Files.IFileTreeNodeViewModel row = new SilverModManager.App.UI.Controls.Trees.Files.FileTreeNodeViewModel(Missing, Missing.Parent, true, 1, 0, FileChangeType.Unrestorable);

        row.FormattedChangeState.Should().Be(Language.IFileTreeNodeViewModel_ToFormattedChangeState_Unrestorable);
        row.FormattedChangeStateToolTip.Should().Be(Language.IFileTreeNodeViewModel_FormattedChangeStateToolTip_Unrestorable);
        row.IsChangeAdded.Should().BeFalse();
    }

    [Fact]
    public async Task TheNoticeListsEachFileWithItsReason()
    {
        var h = await Open("Unwritable Notice", new UnwritableFile(Missing, UnwritableReason.NotOnDisk), new UnwritableFile(Changed, UnwritableReason.DifferentOnDisk));

        await Eventually(() => h.Page.UnwritableFiles.Should().HaveCount(2));
        h.Page.UnwritableFiles.Select(n => n.Text).Should().BeEquivalentTo(
            string.Format(Language.ApplyDiff_Unwritable_NotOnDisk, Missing.Path),
            string.Format(Language.ApplyDiff_Unwritable_DifferentOnDisk, Changed.Path));
    }

    [Fact]
    public async Task EachResolutionIsOfferedOnlyWhenAFileFitsIt()
    {
        var h = await Open("Unwritable Offers", new UnwritableFile(Missing, UnwritableReason.NotOnDisk));

        await Eventually(() => h.Page.UnwritableFiles.Should().HaveCount(1));
        await Eventually(() => h.CanForget.Should().BeTrue());
        h.CanKeep.Should().BeFalse("no unwritable file is on disk");
    }

    [Fact]
    public async Task KeepingTheDiskFileSettlesOnDiskFilesAndRefreshesThePage()
    {
        var h = await Open("Unwritable Keep", new UnwritableFile(Changed, UnwritableReason.DifferentOnDisk), new UnwritableFile(Missing, UnwritableReason.NotOnDisk));
        await Eventually(() => h.CanKeep.Should().BeTrue());

        await Run(h.Page.KeepDiskFilesCommand);

        await h.Sync.Received(1).ResolveUnwritableFiles(h.Loadout.LoadoutId, null, UnwritableResolution.KeepDiskFile, Arg.Any<CancellationToken>());
        await Eventually(() => h.Page.UnwritableFiles.Should().ContainSingle().Which.File.Path.Should().Be(Missing));
        h.Toasts.Received().ShowToast(Language.ApplyDiff_Unwritable_Toast);
    }

    [Fact]
    public async Task ForgettingSettlesMissingFilesAndLeavesAnEmptyNotice()
    {
        var h = await Open("Unwritable Forget", new UnwritableFile(Missing, UnwritableReason.NotOnDisk));
        await Eventually(() => h.CanForget.Should().BeTrue());

        await Run(h.Page.ForgetFilesCommand);

        await h.Sync.Received(1).ResolveUnwritableFiles(h.Loadout.LoadoutId, null, UnwritableResolution.Forget, Arg.Any<CancellationToken>());
        await Eventually(() => h.Page.UnwritableFiles.Should().BeEmpty());
    }

    [Fact]
    public async Task ARefusedResolutionGoesToTheErrorPresenter()
    {
        var h = await Open("Unwritable Refused", new UnwritableFile(Missing, UnwritableReason.NotOnDisk));
        var refusal = new UnwritableResolutionRefusedException("nothing fits");
        h.Sync.ResolveUnwritableFiles(Arg.Any<LoadoutId>(), Arg.Any<IReadOnlyCollection<GamePath>?>(), Arg.Any<UnwritableResolution>(), Arg.Any<CancellationToken>())
            .Throws(refusal);
        await Eventually(() => h.CanForget.Should().BeTrue());

        await Run(h.Page.ForgetFilesCommand);

        await Eventually(async () => await h.Presenter.Received().Report(refusal, Language.ApplyDiff_Unwritable_Activity));
    }

    [Fact]
    public async Task NeitherResolutionRunsWhileAnApplyIsRunning()
    {
        var h = await Open("Unwritable Busy", new UnwritableFile(Missing, UnwritableReason.NotOnDisk));
        await Eventually(() => h.CanForget.Should().BeTrue());

        h.Status.OnNext(LoadoutSynchronizerState.Pending);

        await Eventually(() => h.CanForget.Should().BeFalse());
    }

    [Fact]
    public async Task AnApplyThatLeftFilesOutSaysSoInsteadOfReportingSuccess()
    {
        var sync = Substitute.For<ISynchronizerService>();
        sync.Synchronize(Arg.Any<LoadoutId>(), Arg.Any<BackupOverflowChoice?>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        sync.GetUnwritableFiles(Arg.Any<LoadoutId>()).Returns([new UnwritableFile(Missing, UnwritableReason.NotOnDisk), new UnwritableFile(Changed, UnwritableReason.DifferentOnDisk)]);
        var toasts = Substitute.For<IWindowNotificationService>();
        var services = new ServiceCollection().AddSingleton(sync).AddSingleton(toasts).AddSingleton(Substitute.For<IErrorPresenter>()).BuildServiceProvider();

        var applied = await LoadoutApplier.Apply(services, LoadoutId.From(EntityId.From(1)));

        applied.Should().BeTrue();
        toasts.Received().ShowToast(string.Format(Language.ToastNotification_Applied_With_Unrestorable, 2), ToastNotificationVariant.Neutral, Arg.Any<TimeSpan?>(), Arg.Any<DialogButtonDefinition[]?>(), Arg.Any<Action<ButtonDefinitionId>?>());
        toasts.DidNotReceive().ShowToast(Language.ToastNotification_Applied__0__successfully, ToastNotificationVariant.Success);
    }

    [Fact]
    public async Task AnApplyThatWroteEverythingReportsSuccess()
    {
        var sync = Substitute.For<ISynchronizerService>();
        sync.Synchronize(Arg.Any<LoadoutId>(), Arg.Any<BackupOverflowChoice?>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        sync.GetUnwritableFiles(Arg.Any<LoadoutId>()).Returns([]);
        var toasts = Substitute.For<IWindowNotificationService>();
        var services = new ServiceCollection().AddSingleton(sync).AddSingleton(toasts).AddSingleton(Substitute.For<IErrorPresenter>()).BuildServiceProvider();

        await LoadoutApplier.Apply(services, LoadoutId.From(EntityId.From(1)));

        toasts.Received().ShowToast(Language.ToastNotification_Applied__0__successfully, ToastNotificationVariant.Success);
    }
}
