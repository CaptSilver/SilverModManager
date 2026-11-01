using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using FluentAssertions;
using SilverModManager.App.UI.Windows.Xdnd;
using SilverModManager.UI.Tests.Framework;

namespace SilverModManager.UI.Tests.Windows.Xdnd;

/// <summary>The router raises the four DragDrop routed events the way Avalonia's own drag device does, from window pixels.</summary>
[Collection(HeadlessUi.Collection)]
public class AvaloniaDragRouterTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly PixelPoint FirstCentre = new(50, 50);
    private static readonly PixelPoint SecondCentre = new(150, 50);
    private static readonly PixelPoint ThirdCentre = new(250, 50);

    private sealed class Harness
    {
        public required Window Window { get; init; }
        public required Border First { get; init; }
        public required Border Second { get; init; }
        public required Border Third { get; init; }
        public required Border Parent { get; init; }
        public List<string> Raised { get; } = [];

        /// <summary>What the handlers set DragEffects to; null leaves it at whatever the event came in with.</summary>
        public DragDropEffects? Answer { get; set; }

        public IDataObject? DataSeen { get; set; }
    }

    private static Border Area(string name) => new() { Name = name, Background = Brushes.Gray, Width = 100, Height = 100 };

    private void Watch(Harness h, Border border)
    {
        foreach (var (routed, kind) in new[]
                 {
                     (DragDrop.DragEnterEvent, "Enter"), (DragDrop.DragOverEvent, "Over"),
                     (DragDrop.DragLeaveEvent, "Leave"), (DragDrop.DropEvent, "Drop"),
                 })
        {
            border.AddHandler(routed, (object? _, DragEventArgs e) =>
            {
                h.Raised.Add($"{kind}:{border.Name}");
                h.DataSeen = e.Data;
                if (h.Answer is { } answer) e.DragEffects = answer;
            });
        }
    }

    // First, Second: drop targets. Third: not a target. Parent is a target holding a child that opts out.
    private Harness Show()
    {
        var first = Area("first");
        var second = Area("second");
        var third = Area("third");
        DragDrop.SetAllowDrop(first, true);
        DragDrop.SetAllowDrop(second, true);

        var parent = Area("parent");
        parent.Child = new Border { Name = "optedOut", Background = Brushes.Red, Width = 50, Height = 50, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        DragDrop.SetAllowDrop(parent, true);
        DragDrop.SetAllowDrop(parent.Child, false);
        parent.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;

        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { first, second, third } };
        var column = new StackPanel { Children = { row, parent } };
        var window = Track(new Window { Width = 300, Height = 300, SystemDecorations = SystemDecorations.None, Content = column });
        var h = new Harness { Window = window, First = first, Second = second, Third = third, Parent = parent };
        foreach (var border in new[] { first, second, parent }) Watch(h, border);
        ShowRendered(window);
        return h;
    }

    // Headless windows are always 1x. A platform reports a scale change by invoking the callback the TopLevel handed
    // its impl; ITopLevelImpl hides that property from app code, the headless impl exposes it.
    private static void ChangeScaling(Window window, double scaling)
    {
        var impl = window.PlatformImpl ?? throw new InvalidOperationException("The window is not shown.");
        var callback = impl.GetType().GetProperty("ScalingChanged")?.GetValue(impl) as Action<double>
                       ?? throw new InvalidOperationException($"{impl.GetType()} has no ScalingChanged callback.");
        callback(scaling);
    }

    // Hit testing reads the composited scene, which only catches up on a render tick. Avalonia's own headless drag
    // helpers run one before every event; so does this.
    private async Task Drag(Action<Harness> body)
    {
        var h = await OnUi(() => Task.FromResult(Show()));
        await OnUi(() =>
        {
            Render(h.Window);
            body(h);
        });
    }

    private static AvaloniaDragRouter Router(Harness h) => new(h.Window);

    [Fact]
    public async Task Enter_RaisesDragEnterOnTheControlUnderThePoint_AndReturnsTheEffectTheHandlerSet()
    {
        await Drag(h =>
        {
            h.Answer = DragDropEffects.Move;
            var data = new DataObject();

            var effect = Router(h).Enter(FirstCentre, data, DragDropEffects.Copy | DragDropEffects.Move);

            effect.Should().Be(DragDropEffects.Move);
            h.Raised.Should().Equal("Enter:first");
            h.DataSeen.Should().BeSameAs(data);
        });
    }

    [Fact]
    public async Task Enter_WithAHandlerThatLeavesEffectsAlone_ReturnsTheAllowedEffects()
    {
        await Drag(h =>
        {
            Router(h).Enter(FirstCentre, new DataObject(), DragDropEffects.Link).Should().Be(DragDropEffects.Link);
        });
    }

    [Fact]
    public async Task Over_OnTheSameControl_RaisesDragOver()
    {
        await Drag(h =>
        {
            var router = Router(h);
            router.Enter(FirstCentre, new DataObject(), DragDropEffects.Copy);
            h.Raised.Clear();

            router.Over(new PixelPoint(60, 60), new DataObject(), DragDropEffects.Copy);

            h.Raised.Should().Equal("Over:first");
        });
    }

    [Fact]
    public async Task Over_OnAnotherControl_LeavesTheFirstThenEntersTheSecond()
    {
        await Drag(h =>
        {
            var router = Router(h);
            router.Enter(FirstCentre, new DataObject(), DragDropEffects.Copy);
            h.Raised.Clear();
            h.Answer = DragDropEffects.Link;

            var effect = router.Over(SecondCentre, new DataObject(), DragDropEffects.Copy);

            h.Raised.Should().Equal("Leave:first", "Enter:second");
            effect.Should().Be(DragDropEffects.Link);

            h.Raised.Clear();
            router.Over(new PixelPoint(160, 60), new DataObject(), DragDropEffects.Copy);
            h.Raised.Should().Equal("Over:second");
        });
    }

    [Fact]
    public async Task Over_FromATargetToAPlaceWithoutOne_LeavesTheOldTargetAndRefuses()
    {
        await Drag(h =>
        {
            var router = Router(h);
            router.Enter(FirstCentre, new DataObject(), DragDropEffects.Copy);
            h.Raised.Clear();

            router.Over(ThirdCentre, new DataObject(), DragDropEffects.Copy).Should().Be(DragDropEffects.None);

            h.Raised.Should().Equal("Leave:first");
        });
    }

    [Fact]
    public async Task Leave_RaisesDragLeaveOnTheLastTarget_AndOnlyOnce()
    {
        await Drag(h =>
        {
            var router = Router(h);
            router.Enter(SecondCentre, new DataObject(), DragDropEffects.Copy);
            h.Raised.Clear();

            router.Leave();
            router.Leave();

            h.Raised.Should().Equal("Leave:second");
        });
    }

    [Fact]
    public async Task Leave_WithoutAnEnter_RaisesNothing()
    {
        await Drag(h =>
        {
            Router(h).Leave();
            h.Raised.Should().BeEmpty();
        });
    }

    [Fact]
    public async Task Drop_GoesToTheLastTarget_EvenWhenThePointHasMovedOffIt_AndClearsIt()
    {
        await Drag(h =>
        {
            var router = Router(h);
            router.Enter(FirstCentre, new DataObject(), DragDropEffects.Copy);
            h.Raised.Clear();
            h.Answer = DragDropEffects.Copy;

            var effect = router.Drop(SecondCentre, new DataObject(), DragDropEffects.Copy);

            effect.Should().Be(DragDropEffects.Copy);
            h.Raised.Should().Equal("Drop:first");

            h.Raised.Clear();
            router.Drop(FirstCentre, new DataObject(), DragDropEffects.Copy).Should().Be(DragDropEffects.None);
            router.Leave();
            h.Raised.Should().BeEmpty();
        });
    }

    [Fact]
    public async Task Enter_OutsideAnyDropTarget_RefusesAndRaisesNothing()
    {
        await Drag(h =>
        {
            var router = Router(h);

            router.Enter(ThirdCentre, new DataObject(), DragDropEffects.Copy).Should().Be(DragDropEffects.None);
            router.Drop(ThirdCentre, new DataObject(), DragDropEffects.Copy).Should().Be(DragDropEffects.None);

            h.Raised.Should().BeEmpty();
        });
    }

    [Fact]
    public async Task Enter_OnAChildThatOptsOut_Refuses_BecauseTheDeviceDoesNotWalkUp()
    {
        await Drag(h =>
        {
            // The parent sits in the second row, below the 100px-high first row; its child fills the parent's top-left 50x50.
            var childCentre = new PixelPoint(25, 125);
            var router = Router(h);

            router.Enter(childCentre, new DataObject(), DragDropEffects.Copy).Should().Be(DragDropEffects.None);

            h.Raised.Should().BeEmpty();

            // The parent's own area outside the child still accepts, so the position was on target.
            router.Enter(new PixelPoint(75, 175), new DataObject(), DragDropEffects.Copy).Should().Be(DragDropEffects.Copy);
            h.Raised.Should().Equal("Enter:parent");
        });
    }

    [Fact]
    public async Task Points_AreDividedByTheWindowsRenderScaling()
    {
        await Drag(h =>
        {
            ChangeScaling(h.Window, 2.0);
            h.Window.RenderScaling.Should().Be(2.0);
            var router = Router(h);

            // 300 pixels is 150 DIPs: the second control (100-200 DIPs wide), not the third (200-300).
            router.Enter(new PixelPoint(300, 100), new DataObject(), DragDropEffects.Copy);

            h.Raised.Should().Equal("Enter:second");
        });
    }
}
