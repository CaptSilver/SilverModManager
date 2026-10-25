using System.Reactive.Linq;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Threading;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SilverModManager.App.UI.Controls.Spine;
using SilverModManager.App.UI.Controls.Spine.Buttons.Download;
using SilverModManager.App.UI.Controls.Spine.Buttons.Icon;
using SilverModManager.App.UI.Controls.Spine.Buttons.Image;
using SilverModManager.App.UI.Pages.MyLoadouts;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceAttachments;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Games;

namespace SilverModManager.UI.Tests.Controls.Spine;

public class SpineChangeLoadoutTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public async Task TheLoadoutIconsMenu_StartsWithChangeLoadoutThenASeparatorThenTheMoveItems()
    {
        var button = new ImageButtonViewModel { Name = "Game - Loadout", ChangeLoadout = ReactiveUI.ReactiveCommand.Create(() => { }) };
        var view = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var view = new ImageButton { ViewModel = button };
            Track(new Window { Width = 200, Height = 200, Content = view }).Show();
            return view;
        });

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var items = view.FindControl<Button>("Button")!.ContextMenu!.Items.Cast<Control>().ToArray();
            items.Should().HaveCount(4);
            items[0].Should().BeOfType<MenuItem>().Which.Header.Should().Be(Language.SpineLoadoutMenu_ChangeLoadout);
            items[1].Should().BeOfType<Separator>();
            items[2].Should().BeOfType<MenuItem>().Which.Header.Should().Be(Language.SpineLoadoutMenu_MoveUp);
            items[3].Should().BeOfType<MenuItem>().Which.Header.Should().Be(Language.SpineLoadoutMenu_MoveDown);
        });
    }

    [Fact]
    public async Task ChangeLoadout_OpensMyLoadoutsInTheHomeWorkspaceFilteredToThatIconsGame()
    {
        var loadout = await SeedLoadoutRow(GameStore.Unknown, "Change loadout");
        var windows = Substitute.For<IWindowManager>();
        var controller = Substitute.For<IWorkspaceController>();
        controller.AllWorkspaces.Returns(new ReadOnlyObservableCollection<IWorkspaceViewModel>([]));
        windows.ActiveWorkspaceController.Returns(controller);

        var (spine, activation) = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var spine = new SpineViewModel(
                Provider,
                Provider.GetRequiredService<ILogger<SpineViewModel>>(),
                Connection,
                windows,
                new IconButtonViewModel(),
                new IconButtonViewModel(),
                Substitute.For<ISpineDownloadButtonViewModel>(),
                Substitute.For<IWorkspaceAttachmentsFactoryManager>());
            return (spine, spine.Activator.Activate());
        });
        using (activation)
        {
            IImageButtonViewModel? icon = null;
            await EventuallyOnUi(() =>
            {
                icon = spine.LoadoutSpineItems.Single(item => item.LoadoutBadgeViewModel!.LoadoutValue.Value.LoadoutId == loadout.LoadoutId);
            });

            await OnUi(async () => await icon!.ChangeLoadout!.Execute());
        }

        controller.Received().ChangeOrCreateWorkspaceByContext<HomeContext>(Arg.Any<Func<Optional<PageData>>>());
        var open = controller.ReceivedCalls().Single(call => call.GetMethodInfo().Name == nameof(IWorkspaceController.OpenPage));
        var page = ((Optional<PageData>)open.GetArguments()[1]!).Value;
        page.FactoryId.Should().Be(MyLoadoutsPageFactory.StaticId);
        page.Context.Should().Be(new MyLoadoutsPageContext { InstallationId = loadout.InstallationId });
    }
}
