using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Controls;
using Avalonia.Controls.Selection;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Files.Diff;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.App.UI.Controls.Trees;
using SilverModManager.App.UI.Controls.Trees.Files;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Pages.Diff.ApplyDiff;
using SilverModManager.App.UI.Windows;
using SilverModManager.DataModel.Undo;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.UI.Sdk;
using ReactiveUI;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests;

/// <summary>
/// The Delete command of the Apply preview: when it can run, what the click plans and asks, what an accepted
/// removal does to the loadout and the tree, and where failures go.
/// </summary>
public class ApplyDiffRemoveTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly GamePath GameRoot = new(LocationId.Game, "");
    private static readonly GamePath ModFile = new(LocationId.Game, "archive/pc/mod/a.archive");
    private static readonly GamePath OtherFile = new(LocationId.Game, "archive/pc/mod/b.archive");
    private static readonly GamePath Folder = new(LocationId.Game, "archive/pc/mod");

    private sealed class Harness
    {
        public required ApplyDiffViewModel Page { get; init; }
        public required Loadout.ReadOnly Loadout { get; init; }
        public required IWindowManager Windows { get; init; }
        public required IErrorPresenter Presenter { get; init; }
        public required IWindowNotificationService Toasts { get; init; }
        public required ISynchronizerService Sync { get; init; }
        public required BehaviorSubject<LoadoutSynchronizerState> Status { get; init; }
        public required List<IDialog> Dialogs { get; init; }
        public required List<Exception> Thrown { get; init; }
        public required IDisposable Activation { get; init; }
        public required List<KeyValuePair<GamePath, DiskDiffEntry>> Entries { get; init; }
        public bool CanRun { get; set; }
        public StandardDialogResult Answer { get; set; } = new() { ButtonId = ButtonDefinitionId.Cancel };

        public DiffTreeViewModel Tree => (DiffTreeViewModel)Page.BodyViewModel!;
    }

    private static KeyValuePair<GamePath, DiskDiffEntry> Entry(GamePath path, FileChangeType change) =>
        new(path, new DiskDiffEntry { GamePath = path, Size = Size.From(1), Identity = FileIdentity.XxHash3(Hash.From(1)), ChangeType = change });

    private async Task<Harness> Open(string name, ILoadoutFileRemover? remover = null, params (GamePath Path, FileChangeType Change)[] entries)
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, name);
        var status = new BehaviorSubject<LoadoutSynchronizerState>(LoadoutSynchronizerState.NeedsSync);
        var tree = entries.Select(e => Entry(e.Path, e.Change)).ToList();
        var sync = Substitute.For<ISynchronizerService>();
        sync.GetApplyDiffTree(Arg.Any<LoadoutId>()).Returns(_ => FileDiffTree.Create(tree.ToArray()));
        sync.StatusForLoadout(Arg.Any<LoadoutId>()).Returns(Task.FromResult<IObservable<LoadoutSynchronizerState>>(status));
        var presenter = Substitute.For<IErrorPresenter>();
        presenter.Report(Arg.Any<Exception>(), Arg.Any<string>()).Returns(Task.CompletedTask);
        var toasts = Substitute.For<IWindowNotificationService>();
        var windows = Substitute.For<IWindowManager>();
        var dialogs = new List<IDialog>();
        Harness? harness = null;
        windows.ShowDialog(Arg.Do<IDialog>(d => dialogs.Add(d)), Arg.Any<DialogWindowType>())
            .Returns(_ => Task.FromResult(harness!.Answer));

        var services = new ServiceCollection()
            .AddSingleton(sync)
            .AddSingleton(Connection)
            .AddSingleton(presenter)
            .AddSingleton(toasts)
            .AddSingleton(Substitute.For<SilverModManager.Abstractions.Diagnostics.IDiagnosticManager>())
            .AddSingleton(remover ?? Provider.GetRequiredService<ILoadoutFileRemover>())
            .AddLogging()
            .BuildServiceProvider();

        var thrown = new List<Exception>();
        var page = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var vm = new ApplyDiffViewModel(windows, services);
            vm.Initialize(loadout.LoadoutId);
            return vm;
        });
        var activation = await Dispatcher.UIThread.InvokeAsync(() => page.Activator.Activate());
        page.RemoveSelectedCommand.ThrownExceptions.Subscribe(thrown.Add);
        harness = new Harness
        {
            Page = page, Loadout = loadout, Windows = windows, Presenter = presenter, Toasts = toasts, Sync = sync, Status = status,
            Dialogs = dialogs, Thrown = thrown, Activation = activation, Entries = tree,
        };
        page.RemoveSelectedCommand.CanExecute.Subscribe(can => harness.CanRun = can);
        await Eventually(() => page.BodyViewModel.Should().NotBeNull());
        // The tree binds its rows on the UI thread after the refresh; a selection made before that is reset by it.
        if (entries.Length > 0)
            await Eventually(() => harness.Tree.TreeSource.Items.Should().NotBeEmpty());
        return harness;
    }

    private static FileTreeNodeViewModel Node(GamePath path, FileChangeType change = FileChangeType.Added, bool isFile = true) =>
        new(path, path.Parent, isFile, 1, 0, change);

    private static FileTreeNodeViewModel Root() =>
        new(GameRoot.ToString(), GameRoot, IFileTreeViewModel.RootParentGamePath, false, 0, 0) { ChangeType = FileChangeType.None };

    private static Task Select(Harness h, params IFileTreeNodeViewModel[] nodes) =>
        Dispatcher.UIThread.InvokeAsync(() => h.Tree.SelectedNodes = nodes).GetTask();

    private static Task Press(Harness h) =>
        Dispatcher.UIThread.InvokeAsync(() => { h.Page.RemoveSelectedCommand.Execute().Subscribe(); }).GetTask();

    private static string DialogText(IDialog dialog) =>
        ((SilverModManager.App.UI.Dialog.Dialog)dialog).ViewModel.ContentViewModel.Should().BeAssignableTo<IDialogStandardContentViewModel>().Subject.Text;

    // The database is shared with every other test in the host, so a transaction count says nothing; a loadout's own
    // revisions do.
    private int RestorePoints(Loadout.ReadOnly loadout) => Provider.GetRequiredService<UndoService>().RevisionsFor(loadout.Id).Count;

    private int RowsAt(Loadout.ReadOnly loadout, GamePath path) =>
        LoadoutItemWithTargetPath.FindByTargetPath(Connection.Db, path.ToGamePathParentTuple(loadout.Id)).Count;

    [Fact]
    public async Task TheCommandIsDisabledWithNoSelection()
    {
        var h = await Open("Remove No Selection", entries: (ModFile, FileChangeType.Added));
        await Select(h, Node(ModFile));
        await Eventually(() => h.CanRun.Should().BeTrue());

        await Select(h);

        await Eventually(() => h.CanRun.Should().BeFalse());
    }

    [Fact]
    public async Task TheCommandIsDisabledWhenOnlyRootRowsAreSelected()
    {
        var h = await Open("Remove Only Root", entries: (ModFile, FileChangeType.Added));
        await Select(h, Node(ModFile));
        await Eventually(() => h.CanRun.Should().BeTrue());

        await Select(h, Root());

        await Eventually(() => h.CanRun.Should().BeFalse());
    }

    [Fact]
    public async Task RootRowsInAMixedSelectionAreIgnored()
    {
        var h = await Open("Remove Mixed", entries: (ModFile, FileChangeType.Added));
        await SeedGroup(h.Loadout.LoadoutId, "Mod", files: [ModFile]);
        await Select(h, Root(), Node(ModFile));
        await Eventually(() => h.CanRun.Should().BeTrue());
        h.Answer = new StandardDialogResult { ButtonId = ButtonDefinitionId.Cancel };

        await Press(h);

        await Eventually(() => h.Dialogs.Should().ContainSingle());
        DialogText(h.Dialogs[0]).Should().Contain(ModFile.Path.ToString());
        _ = h.Presenter.DidNotReceiveWithAnyArgs().Report(default!, default!);
    }

    [Fact]
    public async Task TheCommandIsDisabledWhenOnlyRemovedRowsAreSelected()
    {
        var h = await Open("Remove Only Removed", entries: [(ModFile, FileChangeType.Added), (OtherFile, FileChangeType.Removed)]);
        await Select(h, Node(ModFile));
        await Eventually(() => h.CanRun.Should().BeTrue());

        await Select(h, Node(OtherFile, FileChangeType.Removed));

        await Eventually(() => h.CanRun.Should().BeFalse());
    }

    [Fact]
    public async Task TheCommandIsDisabledWhileAnApplyIsRunning()
    {
        var h = await Open("Remove While Applying", entries: (ModFile, FileChangeType.Added));
        await Select(h, Node(ModFile));
        await Eventually(() => h.CanRun.Should().BeTrue("NeedsSync is the normal state of a preview with changes"));

        h.Status.OnNext(LoadoutSynchronizerState.Pending);
        await Eventually(() => h.CanRun.Should().BeFalse());

        h.Status.OnNext(LoadoutSynchronizerState.NeedsSync);
        await Eventually(() => h.CanRun.Should().BeTrue());
        h.Status.OnNext(LoadoutSynchronizerState.Pending);
        await Eventually(() => h.CanRun.Should().BeFalse());
        h.Status.OnNext(LoadoutSynchronizerState.Current);
        await Eventually(() => h.CanRun.Should().BeTrue());
    }

    [Fact]
    public async Task TheCommandIsEnabledForAnAddedRowWithALoadoutProvider()
    {
        var h = await Open("Remove Enabled", entries: (ModFile, FileChangeType.Added));
        await SeedGroup(h.Loadout.LoadoutId, "Mod", files: [ModFile]);

        await Select(h, Node(ModFile));

        await Eventually(() => h.CanRun.Should().BeTrue());
    }

    [Fact]
    public async Task AVanillaRowIsRefusedThroughTheErrorPresenter()
    {
        var h = await Open("Remove Vanilla", entries: (ModFile, FileChangeType.None));
        var restorePoints = RestorePoints(h.Loadout);
        await Select(h, Node(ModFile, FileChangeType.None));
        await Eventually(() => h.CanRun.Should().BeTrue());

        await Press(h);

        await Eventually(() => _ = h.Presenter.Received(1).Report(Arg.Is<Exception>(e => e is FileRemovalRefusedException), Arg.Any<string>()));
        h.Dialogs.Should().BeEmpty("a dialog with a Remove button over nothing helps nobody");
        RestorePoints(h.Loadout).Should().Be(restorePoints, "nothing was written");
        h.Thrown.Should().BeEmpty();
    }

    [Fact]
    public async Task AFolderRowPlansEverythingUnderIt()
    {
        var h = await Open("Remove Folder", entries: [(ModFile, FileChangeType.Added), (OtherFile, FileChangeType.Added)]);
        await SeedGroup(h.Loadout.LoadoutId, "One", files: [ModFile]);
        await SeedGroup(h.Loadout.LoadoutId, "Two", files: [OtherFile]);
        await Select(h, Node(Folder, FileChangeType.Modified, isFile: false));
        await Eventually(() => h.CanRun.Should().BeTrue());

        await Press(h);

        await Eventually(() => h.Dialogs.Should().ContainSingle());
        var text = DialogText(h.Dialogs[0]);
        text.Should().Contain("a.archive — One").And.Contain("b.archive — Two");
    }

    [Fact]
    public async Task MultipleSelectedRowsAreOnePlan()
    {
        var h = await Open("Remove Multiple", entries: [(ModFile, FileChangeType.Added), (OtherFile, FileChangeType.Added)]);
        await SeedGroup(h.Loadout.LoadoutId, "One", files: [ModFile, OtherFile]);
        await Select(h, Node(ModFile), Node(OtherFile));
        await Eventually(() => h.CanRun.Should().BeTrue());

        await Press(h);

        await Eventually(() => h.Dialogs.Should().ContainSingle());
        DialogText(h.Dialogs[0]).Should().Contain("a.archive").And.Contain("b.archive");
    }

    [Fact]
    public async Task CanExecuteOnlyChangesOnTheUiThreadWhenTheStatusTicksOnAPoolThread()
    {
        var h = await Open("Remove Status Thread", entries: (ModFile, FileChangeType.Added));
        await SeedGroup(h.Loadout.LoadoutId, "One", files: [ModFile]);
        await Select(h, Node(ModFile));
        await Eventually(() => h.CanRun.Should().BeTrue());
        var offUi = new System.Collections.Concurrent.ConcurrentBag<bool>();
        // Subscribed on the UI thread: the current value replays on subscribe, on the subscriber's own thread.
        await Dispatcher.UIThread.InvokeAsync(() => h.Page.RemoveSelectedCommand.CanExecute.Subscribe(_ => offUi.Add(!Dispatcher.UIThread.CheckAccess())));

        await Task.Run(() => h.Status.OnNext(LoadoutSynchronizerState.Pending), TestContext.Current.CancellationToken);

        await Eventually(() => h.CanRun.Should().BeFalse());
        offUi.Should().NotContain(true, "the button binds to can-execute, and Avalonia only lets the UI thread change it");
    }

    [Fact]
    public async Task RefreshingClearsTheSelectionWithoutChangingCanExecuteOffTheUiThread()
    {
        var h = await Open("Remove Refresh Thread", entries: (ModFile, FileChangeType.Added));
        await SeedGroup(h.Loadout.LoadoutId, "One", files: [ModFile]);
        await Select(h, Node(ModFile));
        await Eventually(() => h.CanRun.Should().BeTrue());
        var offUi = new System.Collections.Concurrent.ConcurrentBag<bool>();
        // Subscribed on the UI thread: the current value replays on subscribe, on the subscriber's own thread.
        await Dispatcher.UIThread.InvokeAsync(() => h.Page.RemoveSelectedCommand.CanExecute.Subscribe(_ => offUi.Add(!Dispatcher.UIThread.CheckAccess())));

        await Dispatcher.UIThread.InvokeAsync(() => { h.Page.RefreshCommand.Execute().Subscribe(); }).GetTask();

        await Eventually(() => h.CanRun.Should().BeFalse());
        await Eventually(() => h.Page.LoadFailed.Should().BeFalse());
        offUi.Should().NotContain(true);
    }

    [Fact]
    public async Task TheClickPlansAfresh()
    {
        var h = await Open("Remove Fresh", entries: (ModFile, FileChangeType.Added));
        await SeedGroup(h.Loadout.LoadoutId, "First", files: [ModFile]);
        await Select(h, Node(ModFile));
        await Eventually(() => h.CanRun.Should().BeTrue());

        // The tree is rebuilt (new node instances, empty selection) and another mod starts providing the path.
        await Dispatcher.UIThread.InvokeAsync(() => { h.Page.RefreshCommand.Execute().Subscribe(); }).GetTask();
        await Eventually(() => h.Tree.SelectedNodes.Should().BeEmpty());
        await SeedGroup(h.Loadout.LoadoutId, "Second", files: [ModFile]);
        await Select(h, Node(ModFile));
        await Eventually(() => h.CanRun.Should().BeTrue());

        await Press(h);

        await Eventually(() => h.Dialogs.Should().ContainSingle());
        DialogText(h.Dialogs[0]).Should().Contain("— First").And.Contain("— Second");
    }

    [Fact]
    public async Task CancellingTheDialogChangesNothing()
    {
        var h = await Open("Remove Cancel", entries: (ModFile, FileChangeType.Added));
        await SeedGroup(h.Loadout.LoadoutId, "Mod", files: [ModFile]);
        await Select(h, Node(ModFile));
        await Eventually(() => h.CanRun.Should().BeTrue());
        var restorePoints = RestorePoints(h.Loadout);
        h.Answer = new StandardDialogResult { ButtonId = ButtonDefinitionId.Cancel };

        await Press(h);

        await Eventually(() => h.Dialogs.Should().ContainSingle());
        await Task.Delay(300, TestContext.Current.CancellationToken);
        RowsAt(h.Loadout, ModFile).Should().Be(1);
        RestorePoints(h.Loadout).Should().Be(restorePoints, "no restore point is written for a cancelled removal");
        h.Sync.Received(1).GetApplyDiffTree(Arg.Any<LoadoutId>());
        h.Toasts.DidNotReceiveWithAnyArgs().ShowToast(default!);
    }

    [Fact]
    public async Task AcceptingRemovesTheRowsAndRefreshesTheTree()
    {
        var h = await Open("Remove Accept", entries: (ModFile, FileChangeType.Added));
        await SeedGroup(h.Loadout.LoadoutId, "Mod", files: [ModFile]);
        await Select(h, Node(ModFile));
        await Eventually(() => h.CanRun.Should().BeTrue());
        h.Answer = new StandardDialogResult { ButtonId = ButtonDefinitionId.Accept };

        await Press(h);

        await Eventually(() => RowsAt(h.Loadout, ModFile).Should().Be(0));
        await Eventually(() => h.Sync.Received(2).GetApplyDiffTree(Arg.Any<LoadoutId>()));
        await Eventually(() => h.Toasts.ReceivedWithAnyArgs(1).ShowToast(default!));
        _ = h.Presenter.DidNotReceiveWithAnyArgs().Report(default!, default!);
    }

    [Fact]
    public async Task AFailureFromTheServiceGoesToTheErrorPresenterAndNothingIsThrown()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Remove Fault Seed");
        var row = new FileRemovalRow(EntityId.From(1), ModFile, "Mod", LoadoutItemGroupId.From(EntityId.From(2)), null, null, null, false, false, true);
        var remover = Substitute.For<ILoadoutFileRemover>();
        remover.Plan(Arg.Any<FileRemovalRequest>()).Returns(call => new FileRemovalPlan(call.Arg<FileRemovalRequest>(), [row]));
        remover.Remove(Arg.Any<FileRemovalRequest>(), Arg.Any<bool>()).ThrowsAsync(new InvalidOperationException("the commit failed"));
        var h = await Open("Remove Fault", remover, (ModFile, FileChangeType.Added));
        await Select(h, Node(ModFile));
        await Eventually(() => h.CanRun.Should().BeTrue());
        h.Answer = new StandardDialogResult { ButtonId = ButtonDefinitionId.Accept };

        await Press(h);

        await Eventually(() => _ = h.Presenter.Received(1).Report(Arg.Is<Exception>(e => e is InvalidOperationException), Arg.Any<string>()));
        await Task.Delay(200, TestContext.Current.CancellationToken);
        h.Thrown.Should().BeEmpty();
        await remover.Received(1).Remove(Arg.Is<FileRemovalRequest>(r => r.Paths.Single() == ModFile), true);
    }

    [Fact]
    public async Task TheDeleteKeyRunsTheCommand()
    {
        // A page with no tree body: rendering a real tree grid in a headless window keeps the shared UI thread busy
        // for the other tests, and what this checks is only that the view wires the key to the view model's command.
        var runs = 0;
        var page = new KeyProbeViewModel(() => runs++);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var view = new ApplyDiffView { ViewModel = page };
            var window = Track(new Window { Content = view, Width = 400, Height = 300 });
            ShowRendered(window);
            view.FindControl<Control>("RefreshButton")?.Focus();
            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        });

        await Eventually(() => runs.Should().Be(1));
    }

    private sealed class KeyProbeViewModel : APageViewModel<IApplyDiffViewModel>, IApplyDiffViewModel
    {
        public KeyProbeViewModel(Action onRemove) : base(Substitute.For<IWindowManager>())
        {
            RemoveSelectedCommand = ReactiveCommand.Create(onRemove);
        }

        public void Initialize(LoadoutId loadoutId) => throw new NotSupportedException();
        public ReactiveCommand<Unit, Unit> RefreshCommand { get; } = ReactiveCommand.Create(() => { });
        public ReactiveCommand<Unit, Unit> RemoveSelectedCommand { get; }
        public IViewModelInterface? BodyViewModel { get; set; }
        public bool LoadFailed => false;
        public IReadOnlyList<StrayFileNotice> StrayFiles { get; } = [];
        public ReactiveCommand<Unit, Unit> RemoveStrayFilesCommand { get; } = ReactiveCommand.Create(() => { });
    }

    [Fact]
    public async Task SelectingRowsInTheTreeFillsSelectedNodesWithMoreThanOneRow()
    {
        var h = await Open("Remove Tree Selection", entries: [(ModFile, FileChangeType.Added), (OtherFile, FileChangeType.Added)]);
        await Eventually(() => h.Tree.TreeSource.Items.Should().NotBeEmpty());

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var selection = h.Tree.TreeSource.Selection.Should().BeAssignableTo<ITreeDataGridRowSelectionModel<IFileTreeNodeViewModel>>().Subject;
            selection.SingleSelect.Should().BeFalse();
            selection.Select(new IndexPath(0));
            selection.Select(new IndexPath(0, 0));
        });

        await Eventually(() => h.Tree.SelectedNodes.Should().HaveCount(2));
        await Eventually(() => h.CanRun.Should().BeTrue());
    }
}
