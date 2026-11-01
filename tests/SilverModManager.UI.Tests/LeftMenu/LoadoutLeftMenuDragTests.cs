using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using NSubstitute;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.App.UI.LeftMenu.Loadout;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Icons;
using SilverModManager.UI.Tests.Framework;
using MenuStub = SilverModManager.UI.Tests.LeftMenu.LoadoutLeftMenuStub;
using static SilverModManager.UI.Tests.LeftMenu.LeftMenuTestSupport;

namespace SilverModManager.UI.Tests.LeftMenu;

/// <summary>Dragging a collection entry in the left menu: which move it reports, and that a plain click still navigates.</summary>
[Collection(HeadlessUi.Collection)]
public class LoadoutLeftMenuDragTests(IServiceProvider provider) : AUiTest(provider)
{
    private static ILeftMenuItemViewModel Entry(string text, IWorkspaceController controller, PageData page) =>
        new LeftMenuItemViewModel(controller, WorkspaceId.From(Guid.NewGuid()), page) { Text = new StringComponent(text), Icon = IconValues.Download };

    private async Task<(Window Window, LoadoutLeftMenuView View, MenuStub Menu)> Show(IWorkspaceController? controller = null)
    {
        controller ??= Controller();
        var page = new PageData { FactoryId = PageFactoryId.From(Guid.NewGuid()), Context = Substitute.For<IPageFactoryContext>() };
        var menu = new MenuStub(
            Entry("My Mods", controller, page),
            Entry("Alpha", controller, page),
            Entry("Beta", controller, page),
            Entry("Gamma", controller, page)
        );
        return await OnUi(async () =>
        {
            var view = new LoadoutLeftMenuView { ViewModel = menu };
            var window = Track(new Window { Width = 300, Height = 900, Content = view });
            ShowRendered(window);
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
            Render(window);
            return (window, view, menu);
        });
    }

    private static IReadOnlyList<Control> Rows(LoadoutLeftMenuView view) =>
        view.GetVisualDescendants().OfType<ItemsControl>().Single(items => items.Name == "CollectionItems").ItemsPanelRoot!.Children;

    private static Point CentreOf(Control row, Window window) =>
        row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;

    private static async Task Drag(Window window, Point from, double deltaY)
    {
        window.MouseDown(from, MouseButton.Left);
        const int steps = 8;
        for (var i = 1; i <= steps; i++)
        {
            window.MouseMove(from + new Point(0, deltaY * i / steps));
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        window.MouseUp(from + new Point(0, deltaY), MouseButton.Left);
    }

    [Fact]
    public async Task DraggingAnEntryUpPastItsNeighbour_ReportsTheMove()
    {
        var (window, view, menu) = await Show();

        await OnUi(async () =>
        {
            var rows = Rows(view);
            await Drag(window, CentreOf(rows[3], window), -rows[3].Bounds.Height * 1.2);
        });

        await Eventually(() => menu.Moves.Should().Equal([(3, 2)]), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public async Task DraggingAnEntryFarDown_StopsAtTheLastCollection()
    {
        var (window, view, menu) = await Show();

        await OnUi(async () =>
        {
            var rows = Rows(view);
            await Drag(window, CentreOf(rows[1], window), 400);
        });

        await Eventually(() => menu.Moves.Should().Equal([(1, 3)]), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public async Task DraggingAnEntryUpPastTheFixedFirstOne_StopsBelowIt()
    {
        var (window, view, menu) = await Show();

        await OnUi(async () =>
        {
            var rows = Rows(view);
            await Drag(window, CentreOf(rows[2], window), -400);
        });

        await Eventually(() => menu.Moves.Should().Equal([(2, 1)]), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public async Task TheFixedFirstEntry_CannotBeDragged()
    {
        var (window, view, menu) = await Show();

        await OnUi(async () =>
        {
            var rows = Rows(view);
            await Drag(window, CentreOf(rows[0], window), 200);
        });
        await Task.Delay(400);

        menu.Moves.Should().BeEmpty();
    }

    [Fact]
    public async Task ReleasingAnEntryWhereItStarted_ReportsNoMove()
    {
        var (window, view, menu) = await Show();

        await OnUi(async () =>
        {
            var rows = Rows(view);
            var from = CentreOf(rows[2], window);
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(from + new Point(0, 12));
            window.MouseMove(from + new Point(0, 1));
            window.MouseUp(from, MouseButton.Left);
            await Task.CompletedTask;
        });
        await Task.Delay(400);

        menu.Moves.Should().BeEmpty();
    }

    [Fact]
    public async Task AClickWithoutMovement_StillOpensTheEntry()
    {
        var controller = Controller();
        var (window, view, menu) = await Show(controller);

        await OnUi(() =>
        {
            var from = CentreOf(Rows(view)[2], window);
            window.MouseDown(from, MouseButton.Left);
            window.MouseUp(from, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            return Task.CompletedTask;
        });

        await Eventually(() => controller.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IWorkspaceController.OpenPage)).Should().Be(1),
            TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50));
        menu.Moves.Should().BeEmpty();
    }
}
