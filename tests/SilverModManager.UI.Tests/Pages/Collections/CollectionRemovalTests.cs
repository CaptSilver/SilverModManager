using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Avalonia.Threading;
using FluentAssertions;
using NSubstitute;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Pages.Collections;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.UI.Tests.Pages.Collections;

public class CollectionRemovalTests(IServiceProvider provider) : AUiTest(provider)
{
    private static (CollectionRemoval Removal, List<string> Removed) Confirmed(string name = "Some collection")
    {
        var removed = new List<string>();
        var removal = CollectionRemoval.Confirmed("Remove it", IconValues.DeleteOutline, ContextMenuItemStyling.Critical,
            Observable.Return(true), () => name, _ =>
            {
                removed.Add(name);
                return Task.CompletedTask;
            });
        return (removal, removed);
    }

    private static IWindowManager Answering(ButtonDefinitionId button)
    {
        var windows = Substitute.For<IWindowManager>();
        windows.ShowDialog(Arg.Any<IDialog>(), DialogWindowType.Modal).Returns(new StandardDialogResult { ButtonId = button });
        return windows;
    }

    [Fact]
    public async Task ConfirmAndRemoveDoesNothingWhenTheDialogIsCancelled()
    {
        var (removal, removed) = Confirmed();

        await Dispatcher.UIThread.InvokeAsync(() => removal.ConfirmAndRemove(Answering(ButtonDefinitionId.Cancel), Substitute.For<IWorkspaceController>()));

        removed.Should().BeEmpty();
    }

    [Fact]
    public async Task ConfirmAndRemoveRemovesWhenTheDialogIsAccepted()
    {
        var (removal, removed) = Confirmed("Accepted");
        var windows = Answering(ButtonDefinitionId.Accept);

        await Dispatcher.UIThread.InvokeAsync(() => removal.ConfirmAndRemove(windows, Substitute.For<IWorkspaceController>()));

        removed.Should().Equal("Accepted");
        await windows.Received(1).ShowDialog(Arg.Any<IDialog>(), DialogWindowType.Modal);
    }

    [Fact]
    public async Task RemoveRunsWithoutAskingAnything()
    {
        var (removal, removed) = Confirmed("Straight");

        await removal.Remove(Substitute.For<IWorkspaceController>());

        removed.Should().Equal("Straight");
    }

    [Fact]
    public void ARemovalCarriesWhatTheMenuItemShows()
    {
        var (removal, _) = Confirmed();

        removal.Header.Should().Be("Remove it");
        removal.Icon.Should().Be(IconValues.DeleteOutline);
        removal.Styling.Should().Be(ContextMenuItemStyling.Critical);
        removal.ToolTip.Should().BeNull();
    }

    [Fact]
    public async Task TheMenuItemShowsTheRemovalWithoutATooltip()
    {
        var (removal, _) = Confirmed();
        removal = new CollectionRemoval
        {
            Header = removal.Header, Icon = removal.Icon, Styling = removal.Styling, ToolTip = "page only",
            CanExecute = removal.CanExecute, Remove = removal.Remove, ConfirmAndRemove = removal.ConfirmAndRemove,
        };

        var item = removal.ToMenuItem(Substitute.For<IWindowManager>(), Substitute.For<IWorkspaceController>());

        item.Header.Should().Be("Remove it");
        item.Icon.Should().Be(IconValues.DeleteOutline);
        item.Styling.Should().Be(ContextMenuItemStyling.Critical);
        item.ToolTip.Should().BeNull();
        item.IsVisible.Should().BeTrue();
        (await item.Command.CanExecute.FirstAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task TheMenuItemsCommandAsksFirst()
    {
        var (removal, removed) = Confirmed("Asked");
        var windows = Answering(ButtonDefinitionId.Cancel);
        var item = removal.ToMenuItem(windows, Substitute.For<IWorkspaceController>());

        await Dispatcher.UIThread.InvokeAsync(() => item.Command.Execute().FirstOrDefaultAsync().ToTask());
        removed.Should().BeEmpty();

        windows.ShowDialog(Arg.Any<IDialog>(), DialogWindowType.Modal).Returns(new StandardDialogResult { ButtonId = ButtonDefinitionId.Accept });
        await Dispatcher.UIThread.InvokeAsync(() => item.Command.Execute().FirstOrDefaultAsync().ToTask());
        removed.Should().Equal("Asked");
    }

    [Fact]
    public async Task TheMenuItemsCommandFollowsTheRemovalsCanExecute()
    {
        var allowed = new System.Reactive.Subjects.BehaviorSubject<bool>(false);
        var removal = new CollectionRemoval
        {
            Header = "Maybe", Icon = IconValues.DeleteOutline, Styling = ContextMenuItemStyling.Default,
            CanExecute = allowed, Remove = _ => Task.CompletedTask, ConfirmAndRemove = (_, _) => Task.CompletedTask,
        };
        var item = removal.ToMenuItem(Substitute.For<IWindowManager>(), Substitute.For<IWorkspaceController>());
        var seen = new List<bool>();
        using var subscription = item.Command.CanExecute.Subscribe(seen.Add);

        allowed.OnNext(true);

        await Eventually(() => seen.Should().EndWith(true));
        allowed.OnNext(false);
        await Eventually(() => seen.Should().EndWith(false));
    }
}
