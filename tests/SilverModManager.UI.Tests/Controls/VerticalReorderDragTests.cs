using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using SilverModManager.App.UI.Controls;
using SilverModManager.UI.Tests.Framework;

namespace SilverModManager.UI.Tests.Controls;

/// <summary>The vertical reorder drag the left menu and the spine share.</summary>
[Collection(HeadlessUi.Collection)]
public class VerticalReorderDragTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed record Rig(Window Window, ItemsControl Items, ScrollViewer Scroller, Border Outer, VerticalReorderDrag Drag, List<(int From, int To)> Moves);

    private async Task<Rig> Show(Func<int, bool> isMovable, int entries = 4, Visual? unclipUpTo = null)
    {
        var moves = new List<(int From, int To)>();
        return await OnUi(() =>
        {
            var items = new ItemsControl
            {
                ItemsSource = Enumerable.Range(0, entries).ToArray(),
                ItemTemplate = new FuncDataTemplate<int>((_, _) => new Border { Height = 40, Background = Brushes.Gray }),
            };
            var scroller = new ScrollViewer { Content = items, ClipToBounds = true };
            var outer = new Border { Child = scroller, ClipToBounds = true };
            var window = Track(new Window { Width = 300, Height = 400, Content = outer });
            // No waiting on animations in a test.
            window.Resources["SpineReorderSlideDuration"] = TimeSpan.Zero;
            window.Resources["SpineReorderLiftDuration"] = TimeSpan.Zero;
            ShowRendered(window);
            Render(window);
            var drag = new VerticalReorderDrag(items, scroller, isMovable, (from, to) => moves.Add((from, to)), ReorderDragFeel.Menu, unclipUpTo);
            return Task.FromResult(new Rig(window, items, scroller, outer, drag, moves));
        });
    }

    private static Control[] Entries(Rig rig) => rig.Items.ItemsPanelRoot!.Children.ToArray();

    private static Point Below(Control entry, Window window, double fraction) =>
        ReorderDragGestures.CentreIn(entry, window) + new Vector(0, entry.Bounds.Height * fraction);

    [Fact]
    public async Task DraggingAnEntryPastItsNeighbour_CallsMoveWithBothIndices()
    {
        var rig = await Show(_ => true);

        await OnUi(() =>
        {
            var entries = Entries(rig);
            ReorderDragGestures.Drag(rig.Window, ReorderDragGestures.CentreIn(entries[1], rig.Window), Below(entries[2], rig.Window, 0.4));
        });

        await Eventually(() => rig.Moves.Should().Equal([(1, 2)]));
    }

    [Fact]
    public async Task PressingAnEntryOutsideTheMovableRun_NeverStartsADrag()
    {
        var rig = await Show(index => index >= 1);

        await OnUi(() =>
        {
            var entries = Entries(rig);
            ReorderDragGestures.PressAndMove(rig.Window, ReorderDragGestures.CentreIn(entries[0], rig.Window), Below(entries[2], rig.Window, 0.4));

            entries.Should().OnlyContain(entry => entry.RenderTransform == null);
            rig.Window.MouseUp(Below(entries[2], rig.Window, 0.4), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        });

        await Task.Delay(100);
        rig.Moves.Should().BeEmpty();
    }

    // The spine accepts every index, so nothing but the hit test keeps a press on the list's own empty space from
    // being taken for a press on an entry.
    [Fact]
    public async Task PressingBelowEveryEntry_NeverStartsADrag()
    {
        var rig = await Show(_ => true);
        await OnUi(() =>
        {
            rig.Items.MinHeight = 300;
            rig.Items.Background = Brushes.Transparent;
            Render(rig.Window);
        });

        await OnUi(() =>
        {
            var entries = Entries(rig);
            var last = entries[^1];
            var empty = ReorderDragGestures.CentreIn(last, rig.Window) + new Vector(0, last.Bounds.Height * 2);
            ReorderDragGestures.PressAndMove(rig.Window, empty, ReorderDragGestures.CentreIn(entries[1], rig.Window));

            entries.Should().OnlyContain(entry => entry.RenderTransform == null);
            rig.Window.MouseUp(ReorderDragGestures.CentreIn(entries[1], rig.Window), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        });

        await Task.Delay(100);
        rig.Moves.Should().BeEmpty();
    }

    [Fact]
    public async Task AClickWithoutMovement_DoesNotMove()
    {
        var rig = await Show(_ => true);

        await OnUi(() =>
        {
            var at = ReorderDragGestures.CentreIn(Entries(rig)[1], rig.Window);
            ReorderDragGestures.Drag(rig.Window, at, at);
        });

        await Task.Delay(100);
        rig.Moves.Should().BeEmpty();
    }

    [Fact]
    public async Task AfterTheDrop_ClippingIsBackOnEveryAncestorUpToTheStop()
    {
        var rig = await Show(_ => true, unclipUpTo: null);

        await OnUi(() =>
        {
            var entries = Entries(rig);
            var from = ReorderDragGestures.CentreIn(entries[1], rig.Window);
            ReorderDragGestures.PressAndMove(rig.Window, from, Below(entries[2], rig.Window, 0.4));
            rig.Outer.ClipToBounds.Should().BeTrue("the chain stops at the scroll viewer, so what is above it keeps clipping");
            rig.Scroller.ClipToBounds.Should().BeFalse("the lifted entry must not be cut off while it is in the air");
            rig.Window.MouseUp(Below(entries[2], rig.Window, 0.4), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        });

        await EventuallyOnUi(() => rig.Scroller.ClipToBounds.Should().BeTrue("the drop puts the clipping back"));
    }

    [Fact]
    public async Task AnUnclipStopAboveTheScroller_UnclipsThatFarAndNoFurther()
    {
        var rig = await Show(_ => true);
        VerticalReorderDrag stopped = null!;
        await OnUi(() =>
        {
            rig.Drag.Dispose();
            stopped = new VerticalReorderDrag(rig.Items, rig.Scroller, _ => true, (_, _) => { }, ReorderDragFeel.Menu, unclipUpTo: rig.Outer);
        });

        await OnUi(() =>
        {
            var entries = Entries(rig);
            ReorderDragGestures.PressAndMove(rig.Window, ReorderDragGestures.CentreIn(entries[1], rig.Window), Below(entries[2], rig.Window, 0.4));
            rig.Outer.ClipToBounds.Should().BeFalse();
            rig.Window.MouseUp(Below(entries[2], rig.Window, 0.4), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        });

        await EventuallyOnUi(() => rig.Outer.ClipToBounds.Should().BeTrue());
        await OnUi(() => stopped.Dispose());
    }
}
