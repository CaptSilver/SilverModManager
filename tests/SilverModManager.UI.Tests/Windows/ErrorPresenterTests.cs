using System.Reactive.Subjects;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Logging;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Pages.Diagnostics;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Tests.Helpers;

namespace SilverModManager.UI.Tests.Windows;

public class ErrorPresenterTests(IServiceProvider provider) : AUiTest(provider)
{
    private const string StackFrameIndent = "   at ";

    private static readonly OpenPageBehavior LeftMenuBehavior = new OpenPageBehavior.ReplaceTab(Optional<PanelId>.None, Optional<PanelTabId>.None);

    private static async Task<PresenterRig> Pipe(PresenterRig rig, Exception? exception, string message = "job failed")
    {
        var errors = new Subject<LogMessage>();
        rig.Presenter.Connect(errors);
        errors.OnNext(new LogMessage(exception, message));
        await Settled();
        return rig;
    }

    // The pipe's handler runs fire-and-forget on the UI thread; one more trip through it means it has finished.
    private static Task Settled() => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }).GetTask();

    [Fact]
    public async Task ARefusedApply_IsShownAsARefusalWithItsOwnTitle()
    {
        var rig = await Pipe(new PresenterRig(Provider), new SynchronizerPreflightException("The prefix does not exist"));

        var dialog = rig.Dialogs.Should().ContainSingle().Subject;
        dialog.WindowTitle.Should().Be("The loadout was not applied");
        PresenterRig.Body(dialog).Should().Contain("The prefix does not exist");
    }

    [Fact]
    public async Task ASubclassOfTheRefusal_IsShownAsARefusal()
    {
        var rig = await Pipe(new PresenterRig(Provider), new DerivedRefusal());

        rig.Dialogs.Should().ContainSingle().Which.WindowTitle.Should().Be("The loadout was not applied");
    }

    [Fact]
    public async Task ARefusalWrappedInAnAggregate_IsShownAsARefusal()
    {
        var wrapped = new AggregateException(new AggregateException(new SynchronizerPreflightException("read-only")));

        var rig = await Pipe(new PresenterRig(Provider), wrapped);

        var dialog = rig.Dialogs.Should().ContainSingle().Subject;
        dialog.WindowTitle.Should().Be("The loadout was not applied");
        PresenterRig.Body(dialog).Should().Contain("read-only");
    }

    [Fact]
    public async Task AGenuineFailure_IsShownAsACrashWithoutTheTrace()
    {
        var failure = ThrownInvalidOperation("boom");

        var rig = await Pipe(new PresenterRig(Provider), failure);

        var dialog = rig.Dialogs.Should().ContainSingle().Subject;
        PresenterRig.Body(dialog).Should().Contain("job failed").And.Contain("written to the log");
        EverythingRendered(dialog).Should().NotContain(StackFrameIndent).And.NotContain("boom");
    }

    [Fact]
    public async Task ACrashReport_HoldsTheTraceForCopyingButIsNeverRendered()
    {
        var failure = ThrownInvalidOperation("boom");
        var rig = new PresenterRig(Provider);

        await rig.Presenter.Report(failure, "launching the game");

        var dialog = rig.Dialogs.Should().ContainSingle().Subject;
        PresenterRig.Body(dialog).Should().Contain("Something went wrong while launching the game.");
        PresenterRig.Content(dialog).CopyText.Should().Contain(failure.ToString());
        EverythingRendered(dialog).Should().NotContain(StackFrameIndent);
    }

    [Fact]
    public async Task AnErrorWithoutAnException_IsShownAsACrash()
    {
        var rig = await Pipe(new PresenterRig(Provider), null);

        var dialog = rig.Dialogs.Should().ContainSingle().Subject;
        PresenterRig.Body(dialog).Should().Contain("job failed");
    }

    [Fact]
    public async Task ARefusalNextToAGenuineFailure_IsShownAsACrash()
    {
        var mixed = new AggregateException(new SynchronizerPreflightException("read-only"), new InvalidOperationException("boom"));

        var rig = await Pipe(new PresenterRig(Provider), mixed);

        var dialog = rig.Dialogs.Should().ContainSingle().Subject;
        dialog.WindowTitle.Should().NotBe("The loadout was not applied");
        PresenterRig.Body(dialog).Should().Contain("written to the log");
    }

    [Fact]
    public async Task TheSameExceptionReportedTwice_GivesOneDialog()
    {
        var rig = new PresenterRig(Provider);
        var refusal = new SynchronizerPreflightException("read-only");

        await rig.Presenter.Report(refusal, "applying");
        await rig.Presenter.Report(refusal, "applying");

        rig.Dialogs.Should().ContainSingle();
    }

    [Fact]
    public async Task AWrapperThenItsLeaf_GiveOneDialog()
    {
        var rig = new PresenterRig(Provider);
        var leaf = new SynchronizerPreflightException("read-only");

        await rig.Presenter.Report(new AggregateException(leaf), "applying");
        await rig.Presenter.Report(leaf, "applying");

        rig.Dialogs.Should().ContainSingle();
    }

    [Fact]
    public async Task ACatchSiteReportFollowedByThePipe_GivesOneDialog()
    {
        var rig = new PresenterRig(Provider);
        var failure = new InvalidOperationException("boom");
        var errors = new Subject<LogMessage>();
        rig.Presenter.Connect(errors);

        await rig.Presenter.Report(failure, "launching the game");
        errors.OnNext(new LogMessage(failure, "Job 3 of type RunGameTool failed"));
        await Settled();

        rig.Dialogs.Should().ContainSingle();
    }

    [Fact]
    public async Task ACatchSiteReport_IsLoggedWithTheExceptionAtTheLevelOfItsKind()
    {
        var rig = new PresenterRig(Provider);
        var refusal = new SynchronizerPreflightException("read-only");
        var crash = new InvalidOperationException("boom");

        await rig.Presenter.Report(refusal, "applying");
        await rig.Presenter.Report(crash, "applying");

        rig.Log.Entries.Should().Equal((LogLevel.Warning, refusal), (LogLevel.Error, crash));
    }

    [Fact]
    public async Task TheActionButton_IsOfferedIfAndOnlyIfTheRefusalCarriesAnAction()
    {
        var rig = new PresenterRig(Provider);
        var plain = new SynchronizerPreflightException("one");
        var withAction = new SynchronizerPreflightException("two") { Action = new OpenHealthCheck(LoadoutId.From(1)) };

        await rig.Presenter.Report(plain, "applying");
        await rig.Presenter.Report(withAction, "applying");

        rig.Dialogs[0].ButtonDefinitions.Select(b => b.Text).Should().Equal("OK");
        rig.Dialogs[1].ButtonDefinitions.Select(b => b.Text).Should().Equal("OK", "Open Health Check");
    }

    [Fact]
    public async Task OpenHealthCheck_OpensTheLoadoutsDiagnosticList()
    {
        var loadout = await SeedLoadoutRow(SilverModManager.Sdk.Games.GameStore.Unknown, "Health " + Guid.NewGuid());
        var workspace = Substitute.For<IWorkspaceViewModel>();
        workspace.Id.Returns(WorkspaceId.From(Guid.NewGuid()));
        var workspaces = Substitute.For<IWorkspaceController>();
        workspaces.GetOpenPageBehavior(Arg.Any<PageData>(), Arg.Any<NavigationInformation>()).Returns(LeftMenuBehavior);
        workspaces.ChangeOrCreateWorkspaceByContext(Arg.Any<Func<LoadoutContext, bool>>(), Arg.Any<Func<Optional<PageData>>>(), Arg.Any<Func<LoadoutContext>>())
            .Returns(workspace);
        var rig = new PresenterRig(Provider);
        rig.Windows.ActiveWorkspaceController.Returns(workspaces);
        rig.Answer = _ => Task.FromResult(new StandardDialogResult { ButtonId = ErrorPresenter.OpenHealthCheckButton });

        await rig.Presenter.Report(new SynchronizerPreflightException("no vanilla") { Action = new OpenHealthCheck(loadout.LoadoutId) }, "applying");

        workspaces.Received(1).OpenPage(
            Arg.Any<WorkspaceId>(),
            Arg.Is<Optional<PageData>>(page => page.HasValue
                && page.Value.FactoryId == DiagnosticListPageFactory.StaticId
                && ((DiagnosticListPageContext)page.Value.Context).LoadoutId == loadout.LoadoutId),
            LeftMenuBehavior, Arg.Any<bool>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task OkOnARefusal_LeavesTheWorkspaceAlone()
    {
        var workspaces = Substitute.For<IWorkspaceController>();
        var rig = new PresenterRig(Provider);
        rig.Windows.ActiveWorkspaceController.Returns(workspaces);

        await rig.Presenter.Report(new SynchronizerPreflightException("no vanilla") { Action = new OpenHealthCheck(LoadoutId.From(1)) }, "applying");

        workspaces.DidNotReceiveWithAnyArgs().OpenPage(default, default, default!);
    }

    [Fact]
    public async Task WithShowExceptionsOff_ACrashIsHiddenButARefusalIsNot()
    {
        var rig = new PresenterRig(Provider, showExceptions: false);

        await rig.Presenter.Report(new InvalidOperationException("boom"), "applying");
        rig.Dialogs.Should().BeEmpty();

        await rig.Presenter.Report(new SynchronizerPreflightException("read-only"), "applying");
        rig.Dialogs.Should().ContainSingle();
    }

    [Fact]
    public async Task ACrashDialogOffersTheLogFolder()
    {
        var rig = new PresenterRig(Provider);
        rig.Answer = _ => Task.FromResult(new StandardDialogResult { ButtonId = ErrorPresenter.OpenLogFolderButton });

        await rig.Presenter.Report(new InvalidOperationException("boom"), "applying");

        rig.Dialogs.Single().ButtonDefinitions.Select(b => b.Text).Should().Equal("OK", "Open log folder");
        rig.Os.ReceivedWithAnyArgs(1).OpenDirectory(default);
    }

    [Fact]
    public async Task CrashesWhileACrashDialogIsOpen_RaiseItsCounterInsteadOfOpeningMore()
    {
        var closed = new TaskCompletionSource<StandardDialogResult>();
        var rig = new PresenterRig(Provider) { Answer = _ => closed.Task };

        var first = rig.Presenter.Report(new InvalidOperationException("one"), "importing");
        await Settled();
        await rig.Presenter.Report(new InvalidOperationException("two"), "importing");
        await rig.Presenter.Report(new InvalidOperationException("three"), "importing");

        var dialog = rig.Dialogs.Should().ContainSingle().Subject;
        PresenterRig.Body(dialog).Should().Contain("2 more");
        closed.SetResult(new StandardDialogResult { ButtonId = ButtonDefinitionIdAccept });
        await first;
    }

    [Fact]
    public async Task APipeEventFromABackgroundThread_IsShownOnTheUiThread()
    {
        var rig = new PresenterRig(Provider);
        var errors = new Subject<LogMessage>();
        rig.Presenter.Connect(errors);

        await Task.Run(() => errors.OnNext(new LogMessage(new SynchronizerPreflightException("read-only"), "job failed")));
        await Eventually(() => rig.Dialogs.Should().ContainSingle());

        rig.AllOnUiThread.Should().BeTrue();
    }

    [Fact]
    public async Task ConnectingTwice_DoesNotShowEachEventTwice()
    {
        var rig = new PresenterRig(Provider);
        var errors = new Subject<LogMessage>();
        rig.Presenter.Connect(errors);
        rig.Presenter.Connect(errors);

        errors.OnNext(new LogMessage(new InvalidOperationException("boom"), "job failed"));
        await Settled();

        rig.Dialogs.Should().ContainSingle();
    }

    private static readonly ButtonDefinitionId ButtonDefinitionIdAccept = ButtonDefinitionId.Accept;

    // The text of every field the dialog draws; the copy text is deliberately not among them.
    private static string EverythingRendered(DialogViewModel dialog)
    {
        var content = PresenterRig.Content(dialog);
        return string.Join('\n', new[] { dialog.WindowTitle, content.Heading, content.Text, content.BottomText, content.MarkdownRenderer?.Contents }
            .Concat(dialog.ButtonDefinitions.Select(b => b.Text)));
    }

    private static InvalidOperationException ThrownInvalidOperation(string message)
    {
        try { throw new InvalidOperationException(message); }
        catch (InvalidOperationException e) { return e; }
    }

    private class DerivedRefusal() : SynchronizerPreflightException("refused");
}
