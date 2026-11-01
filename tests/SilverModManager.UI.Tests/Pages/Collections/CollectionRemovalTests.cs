using System.Reactive.Linq;
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
}
