using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Files.Diff;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Pages.Diff.ApplyDiff;
using SilverModManager.App.UI.Windows;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests;

/// <summary>
/// The notice on the Apply preview for files the game folder holds and no mod provides: what lists it, what the Delete
/// button asks and removes, and when it goes away.
/// </summary>
public class ApplyDiffStrayFilesTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly GamePath Dll = new(LocationId.Game, "Data/SKSE/Plugins/old.dll");
    private static readonly GamePath OtherDll = new(LocationId.Game, "Data/SKSE/Plugins/other.dll");

    private static readonly KeyValuePair<GamePath, DiskDiffEntry> Entry = new(Dll, new DiskDiffEntry
    {
        GamePath = Dll, Size = NexusMods.Paths.Size.From(1), Identity = SilverModManager.Sdk.Hashes.FileIdentity.XxHash3(NexusMods.Hashing.xxHash3.Hash.From(1)), ChangeType = FileChangeType.Added,
    });

    private readonly List<IDisposable> _activations = [];

    public override async ValueTask DisposeAsync()
    {
        // A page left active keeps following its diagnostics and status after the test is over.
        await Dispatcher.UIThread.InvokeAsync(() => _activations.ForEach(a => a.Dispose()));
        await base.DisposeAsync();
    }

    private sealed class Harness
    {
        public required ApplyDiffViewModel Page { get; init; }
        public required Loadout.ReadOnly Loadout { get; init; }
        public required BehaviorSubject<Diagnostic[]> Diagnostics { get; init; }
        public required BehaviorSubject<LoadoutSynchronizerState> Status { get; init; }
        public required ILoadoutFileRemover Remover { get; init; }
        public required IWindowNotificationService Toasts { get; init; }
        public required List<IDialog> Dialogs { get; init; }
        public required IDisposable Activation { get; init; }
        public bool CanRun { get; set; }
        public StandardDialogResult Answer { get; set; } = new() { ButtonId = ButtonDefinitionId.Cancel };
    }

    private static Diagnostic Stray(GamePath path, LoadoutItemGroupId group, bool withAction = true) => new()
    {
        Id = new DiagnosticId("SilverModManager.UI.Tests", 9),
        Title = "Stray",
        Severity = DiagnosticSeverity.Warning,
        Summary = DiagnosticMessage.From("Summary"),
        Details = DiagnosticMessage.From("Details"),
        DataReferences = [],
        Actions = withAction ? [DiagnosticActions.RemoveLoadoutFile(path, group.Value)] : [DiagnosticActions.UseLoadoutFromSteam()],
    };

    private async Task<Harness> Open(string name, params Diagnostic[] initial)
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, name);
        var diagnostics = new BehaviorSubject<Diagnostic[]>(initial);
        var status = new BehaviorSubject<LoadoutSynchronizerState>(LoadoutSynchronizerState.NeedsSync);
        var sync = Substitute.For<ISynchronizerService>();
        sync.GetApplyDiffTree(Arg.Any<LoadoutId>()).Returns(_ => FileDiffTree.Create([Entry]));
        sync.StatusForLoadout(Arg.Any<LoadoutId>()).Returns(Task.FromResult<IObservable<LoadoutSynchronizerState>>(status));
        var manager = Substitute.For<IDiagnosticManager>();
        manager.GetLoadoutDiagnostics(Arg.Any<LoadoutId>()).Returns(diagnostics);
        var presenter = Substitute.For<IErrorPresenter>();
        presenter.Report(Arg.Any<Exception>(), Arg.Any<string>()).Returns(Task.CompletedTask);
        var toasts = Substitute.For<IWindowNotificationService>();
        var remover = Substitute.For<ILoadoutFileRemover>();
        remover.Plan(Arg.Any<FileRemovalRequest>()).Returns(call => new FileRemovalPlan(call.Arg<FileRemovalRequest>(),
            call.Arg<FileRemovalRequest>().Paths.Select(p => new FileRemovalRow(EntityId.From(1), p, "Overrides", LoadoutItemGroupId.From(EntityId.From(2)), null, null, null, false, false, true)).ToArray()));
        var dialogs = new List<IDialog>();
        Harness? harness = null;
        var windows = Substitute.For<IWindowManager>();
        windows.ShowDialog(Arg.Do<IDialog>(d => dialogs.Add(d)), Arg.Any<DialogWindowType>()).Returns(_ => Task.FromResult(harness!.Answer));

        var services = new ServiceCollection()
            .AddSingleton(sync).AddSingleton(Connection).AddSingleton(presenter).AddSingleton(toasts)
            .AddSingleton(manager).AddSingleton(remover).AddLogging()
            .BuildServiceProvider();

        var page = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var vm = new ApplyDiffViewModel(windows, services);
            vm.Initialize(loadout.LoadoutId);
            return vm;
        });
        var activation = await Dispatcher.UIThread.InvokeAsync(() => page.Activator.Activate());
        _activations.Add(activation);
        harness = new Harness
        {
            Page = page, Loadout = loadout, Diagnostics = diagnostics, Status = status, Remover = remover, Toasts = toasts,
            Dialogs = dialogs, Activation = activation,
        };
        page.RemoveStrayFilesCommand.CanExecute.Subscribe(can => harness.CanRun = can);
        return harness;
    }

    private static Task Press(Harness h) =>
        Dispatcher.UIThread.InvokeAsync(() => { h.Page.RemoveStrayFilesCommand.Execute().Subscribe(); }).GetTask();

    private static readonly LoadoutItemGroupId Group = LoadoutItemGroupId.From(EntityId.From(0x77));

    [Fact]
    public async Task ADiagnosticWithTheActionIsListed()
    {
        var h = await Open("Stray Listed", Stray(Dll, Group));

        await Eventually(() => h.Page.StrayFiles.Should().ContainSingle().Which.Should().Be(new StrayFileNotice(Dll, Group)));
        h.Page.StrayFiles[0].FileName.Should().Be("old.dll");
    }

    [Fact]
    public async Task ADiagnosticWithoutTheActionIsNot()
    {
        var h = await Open("Stray Not Listed", Stray(Dll, Group, withAction: false));

        await Task.Delay(200, TestContext.Current.CancellationToken);
        h.Page.StrayFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task AnEmptyListHidesTheNotice()
    {
        var h = await Open("Stray Emptied", Stray(Dll, Group));
        await Eventually(() => h.Page.StrayFiles.Should().HaveCount(1));

        h.Diagnostics.OnNext([]);

        await Eventually(() => h.Page.StrayFiles.Should().BeEmpty());
    }

    [Fact]
    public async Task AcceptingRemovesTheRowsScopedToTheActionsGroups()
    {
        var h = await Open("Stray Accept", Stray(Dll, Group), Stray(OtherDll, Group));
        await Eventually(() => h.Page.StrayFiles.Should().HaveCount(2));
        h.Answer = new StandardDialogResult { ButtonId = ButtonDefinitionId.Accept };

        await Press(h);

        await Eventually(() => h.Remover.Received(1).Remove(
            Arg.Is<FileRemovalRequest>(r => r.Paths.Length == 2 && r.OnlyInGroups != null && r.OnlyInGroups.Single() == Group), true));
        await Eventually(() => h.Toasts.ReceivedWithAnyArgs(1).ShowToast(default!));
    }

    [Fact]
    public async Task EachNoticeIsRemovedOnlyFromItsOwnGroup()
    {
        var otherGroup = LoadoutItemGroupId.From(EntityId.From(0x78));
        var h = await Open("Stray Two Groups", Stray(Dll, Group), Stray(OtherDll, otherGroup));
        await Eventually(() => h.Page.StrayFiles.Should().HaveCount(2));
        h.Answer = new StandardDialogResult { ButtonId = ButtonDefinitionId.Accept };

        await Press(h);

        // One request carrying both paths and both groups would also remove Dll from otherGroup and OtherDll from Group.
        await Eventually(() => h.Remover.Received(1).Remove(
            Arg.Is<FileRemovalRequest>(r => r.Paths.SequenceEqual(new[] { Dll }) && r.OnlyInGroups!.SequenceEqual(new[] { Group })), true));
        await Eventually(() => h.Remover.Received(1).Remove(
            Arg.Is<FileRemovalRequest>(r => r.Paths.SequenceEqual(new[] { OtherDll }) && r.OnlyInGroups!.SequenceEqual(new[] { otherGroup })), true));
        await h.Remover.DidNotReceive().Remove(Arg.Is<FileRemovalRequest>(r => r.Paths.Length > 1 && r.OnlyInGroups!.Length > 1), true);
    }

    [Fact]
    public async Task CancellingRemovesNothing()
    {
        var h = await Open("Stray Cancel", Stray(Dll, Group));
        await Eventually(() => h.Page.StrayFiles.Should().HaveCount(1));

        await Press(h);

        await Eventually(() => h.Dialogs.Should().ContainSingle());
        await h.Remover.DidNotReceiveWithAnyArgs().Remove(default!, default);
    }

    [Fact]
    public async Task TheCommandIsOffWhileAnApplyRuns()
    {
        var h = await Open("Stray Applying", Stray(Dll, Group));
        await Eventually(() => h.CanRun.Should().BeTrue());

        h.Status.OnNext(LoadoutSynchronizerState.Pending);
        await Eventually(() => h.CanRun.Should().BeFalse());

        h.Status.OnNext(LoadoutSynchronizerState.Current);
        await Eventually(() => h.CanRun.Should().BeTrue());
    }
}
