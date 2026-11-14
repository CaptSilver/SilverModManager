using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using FluentAssertions;
using NSubstitute;
using SilverModManager.App.UI.Input;
using SilverModManager.App.UI.Input.Bindings;
using SilverModManager.Platform.Gamepad;
using SilverModManager.UI.Tests.Framework;

namespace SilverModManager.UI.Tests.Input;

[Collection(HeadlessUi.Collection)]
public class PadBindingsTests(IServiceProvider provider) : AUiTest(provider)
{
    private static IEnumerable<IPadBinding> Moving() => [new FocusMoveBinding(), new ActivateBinding(), new BackBinding()];

    private static StackPanel Panel(Orientation orientation, params Control[] children)
    {
        var panel = new StackPanel { Orientation = orientation };
        XYFocus.SetNavigationModes(panel, XYFocusNavigationModes.Enabled);
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    [Fact]
    public async Task XOpensTheFocusedControlsContextFlyout()
    {
        await OnUi(() =>
        {
            var flyout = new Flyout { Content = new TextBlock { Text = "menu" } };
            var button = new Button { Content = "target", ContextFlyout = flyout };
            using var rig = new PadRig(Panel(Orientation.Vertical, button), () => [new ContextMenuBinding()]);
            rig.Show();
            button.Focus(NavigationMethod.Tab);

            rig.Send(new ButtonDown(TimeSpan.Zero, GamepadButton.X));

            flyout.IsOpen.Should().BeTrue();
            flyout.Hide();
        });
    }

    [Fact]
    public async Task XDoesNothingWhenNothingIsFocused()
    {
        await OnUi(() =>
        {
            var binding = new ContextMenuBinding();
            using var rig = new PadRig(Panel(Orientation.Vertical, new Button()), () => [binding]);
            rig.Show();
            rig.Window.FocusManager!.ClearFocus();

            binding.CanExecute(rig.Navigator.CurrentContext()!).Should().BeFalse();
        });
    }

    [Fact]
    public async Task InsideAListDownMovesTheSelectionNotTheFocus()
    {
        await OnUi(() =>
        {
            var list = new ListBox { ItemsSource = new[] { "one", "two", "three" }, SelectedIndex = 0 };
            var below = new Button { Content = "below" };
            using var rig = new PadRig(Panel(Orientation.Vertical, list, below), () => Moving());
            rig.Show();
            FocusRow(list, list.SelectedIndex);

            rig.Send(new ButtonDown(TimeSpan.Zero, GamepadButton.DPadDown));

            list.SelectedIndex.Should().Be(1);
            below.IsFocused.Should().BeFalse();
        });
    }

    [Fact]
    public async Task DownAtTheLastRowOfAListStaysInTheList()
    {
        await OnUi(() =>
        {
            var list = new ListBox { ItemsSource = new[] { "one", "two" }, SelectedIndex = 1 };
            var below = new Button { Content = "below" };
            using var rig = new PadRig(Panel(Orientation.Vertical, list, below), () => Moving());
            rig.Show();
            FocusRow(list, list.SelectedIndex);

            rig.Send(new ButtonDown(TimeSpan.Zero, GamepadButton.DPadDown));

            below.IsFocused.Should().BeFalse("the list uses Down itself, so it does not hand focus to the control under it");
        });
    }

    [Fact]
    public async Task LeftLeavesTheList()
    {
        await OnUi(() =>
        {
            var left = new Button { Content = "left" };
            var list = new ListBox { ItemsSource = new[] { "one", "two" }, SelectedIndex = 0, Width = 150, Height = 100 };
            using var rig = new PadRig(Panel(Orientation.Horizontal, left, list), () => Moving());
            rig.Show();
            FocusRow(list, list.SelectedIndex);

            rig.Send(new ButtonDown(TimeSpan.Zero, GamepadButton.DPadLeft));

            left.IsFocused.Should().BeTrue();
        });
    }

    [Fact]
    public async Task DownLeavesAClosedComboBoxWithoutChangingItsSelection()
    {
        await OnUi(() =>
        {
            var combo = new ComboBox { ItemsSource = new[] { "a", "b", "c" }, SelectedIndex = 0 };
            var below = new Button { Content = "below" };
            using var rig = new PadRig(Panel(Orientation.Vertical, combo, below), () => Moving());
            rig.Show();
            combo.Focus(NavigationMethod.Tab);

            rig.Send(new ButtonDown(TimeSpan.Zero, GamepadButton.DPadDown));

            below.IsFocused.Should().BeTrue();
            combo.SelectedIndex.Should().Be(0);
        });
    }

    [Fact]
    public async Task ATextBoxKeepsLeftAndRightForTheCaret()
    {
        await OnUi(() =>
        {
            var box = new TextBox { Text = "abc", CaretIndex = 0 };
            var after = new Button { Content = "after" };
            using var rig = new PadRig(Panel(Orientation.Horizontal, box, after), () => Moving());
            rig.Show();
            box.Focus(NavigationMethod.Tab);
            box.SelectionStart = box.SelectionEnd = box.CaretIndex = 0;

            rig.Send(new ButtonDown(TimeSpan.Zero, GamepadButton.DPadRight));

            box.CaretIndex.Should().Be(1);
            after.IsFocused.Should().BeFalse();
        });
    }

    [Fact]
    public async Task ATextBoxLetsUpAndDownMoveFocus()
    {
        await OnUi(() =>
        {
            var box = new TextBox { Text = "abc" };
            var below = new Button { Content = "below" };
            using var rig = new PadRig(Panel(Orientation.Vertical, box, below), () => Moving());
            rig.Show();
            box.Focus(NavigationMethod.Tab);

            rig.Send(new ButtonDown(TimeSpan.Zero, GamepadButton.DPadDown));

            below.IsFocused.Should().BeTrue();
        });
    }

    [Fact]
    public async Task TheRightStickScrollsTheScrollViewerAroundTheFocus()
    {
        await OnUi(() =>
        {
            var tall = new StackPanel();
            for (var i = 0; i < 40; i++) tall.Children.Add(new Button { Content = $"row {i}", Height = 40 });
            var scroller = new ScrollViewer { Content = tall };
            using var rig = new PadRig(scroller, () => [new ScrollBinding()]);
            rig.Show();
            ((Button)tall.Children[0]).Focus(NavigationMethod.Tab);

            rig.Send(new Stick(TimeSpan.Zero, StickSide.Right, 0, 1f));

            scroller.Offset.Y.Should().BeGreaterThan(0);

            rig.Send(new Stick(TimeSpan.Zero, StickSide.Right, 0, 0));
        });
    }

    [Fact]
    public async Task DeactivatingTheWindowStopsAHeldStickScrolling()
    {
        await OnUi(() =>
        {
            var tall = new StackPanel();
            for (var i = 0; i < 40; i++) tall.Children.Add(new Button { Content = $"row {i}", Height = 40 });
            var scroller = new ScrollViewer { Content = tall };
            var binding = new ScrollBinding();
            using var rig = new PadRig(scroller, () => [binding]);
            rig.Show();
            ((Button)tall.Children[0]).Focus(NavigationMethod.Tab);
            rig.Send(new Stick(TimeSpan.Zero, StickSide.Right, 0, 1f));
            binding.IsScrolling.Should().BeTrue();

            rig.Navigator.WindowActivationChanged(false);

            binding.IsScrolling.Should().BeFalse("the stick's release goes to a paused source and never arrives");
        });
    }

    [Fact]
    public async Task ASmallRightStickDeflectionDoesNotScroll()
    {
        await OnUi(() =>
        {
            var tall = new StackPanel();
            for (var i = 0; i < 40; i++) tall.Children.Add(new Button { Content = $"row {i}", Height = 40 });
            var scroller = new ScrollViewer { Content = tall };
            using var rig = new PadRig(scroller, () => [new ScrollBinding()]);
            rig.Show();
            ((Button)tall.Children[0]).Focus(NavigationMethod.Tab);

            rig.Send(new Stick(TimeSpan.Zero, StickSide.Right, 0, 0.1f));

            scroller.Offset.Y.Should().Be(0);
        });
    }

    [Fact]
    public async Task RightTriggerPagesDownAndLeftTriggerPagesBackUp()
    {
        await OnUi(() =>
        {
            var tall = new StackPanel();
            for (var i = 0; i < 40; i++) tall.Children.Add(new Button { Content = $"row {i}", Height = 40 });
            var scroller = new ScrollViewer { Content = tall };
            using var rig = new PadRig(scroller, () => [new ScrollBinding()]);
            rig.Show();
            ((Button)tall.Children[0]).Focus(NavigationMethod.Tab);

            rig.Send(new ButtonDown(TimeSpan.Zero, GamepadButton.RT));
            var paged = scroller.Offset.Y;
            paged.Should().BeGreaterThan(0);

            rig.Send(new ButtonDown(TimeSpan.Zero, GamepadButton.LT));
            scroller.Offset.Y.Should().BeLessThan(paged);
        });
    }

    [Fact]
    public async Task RbSelectsTheNextTabAndLbThePreviousOne()
    {
        await OnUi(() =>
        {
            var tabs = new TabControl
            {
                Width = 300,
                Height = 200,
                ItemsSource = new[]
                {
                    new TabItem { Header = "one", Content = new Button { Content = "in one" } },
                    new TabItem { Header = "two", Content = new Button { Content = "in two" } },
                    new TabItem { Header = "three", Content = new Button { Content = "in three" } },
                },
            };
            using var rig = new PadRig(tabs, () => [new TabCycleBinding(forward: true), new TabCycleBinding(forward: false)]);
            rig.Show();
            ((TabItem)tabs.Items[0]!).Focus(NavigationMethod.Tab);

            rig.Send(new ButtonDown(TimeSpan.Zero, GamepadButton.RB));
            tabs.SelectedIndex.Should().Be(1);

            rig.Send(new ButtonDown(TimeSpan.Zero, GamepadButton.LB));
            tabs.SelectedIndex.Should().Be(0);
        });
    }

    [Fact]
    public async Task LbOnTheFirstTabStaysOnTheFirstTab()
    {
        await OnUi(() =>
        {
            var tabs = new TabControl
            {
                Width = 300,
                Height = 200,
                ItemsSource = new[] { new TabItem { Header = "one" }, new TabItem { Header = "two" } },
            };
            using var rig = new PadRig(tabs, () => [new TabCycleBinding(forward: false)]);
            rig.Show();
            ((TabItem)tabs.Items[0]!).Focus(NavigationMethod.Tab);

            rig.Send(new ButtonDown(TimeSpan.Zero, GamepadButton.LB));

            tabs.SelectedIndex.Should().Be(0);
        });
    }

    [Fact]
    public async Task TheTabBindingStepsAsideWhenThereIsNoTabControl()
    {
        await OnUi(() =>
        {
            var binding = new TabCycleBinding(forward: true);
            var button = new Button();
            using var rig = new PadRig(Panel(Orientation.Vertical, button), () => [binding]);
            rig.Show();
            button.Focus(NavigationMethod.Tab);

            binding.CanExecute(rig.Navigator.CurrentContext()!).Should().BeFalse();
        });
    }

    [Fact]
    public async Task InPadModeAComboBoxPopupUsesTheOverlayLayer()
    {
        await OnUi(() =>
        {
            var combo = new ComboBox { ItemsSource = new[] { "a", "b" }, SelectedIndex = 0 };
            using var rig = new PadRig(Panel(Orientation.Vertical, combo), () => Moving());
            rig.Show();
            rig.Mode.PadUsed();
            RunPending();

            combo.IsDropDownOpen = true;
            RunPending();
            var inPad = ComboPopup(combo).ShouldUseOverlayLayer;
            combo.IsDropDownOpen = false;
            RunPending();

            rig.Mode.PointerMoved();
            RunPending();
            combo.IsDropDownOpen = true;
            RunPending();
            var withPointer = ComboPopup(combo).ShouldUseOverlayLayer;
            combo.IsDropDownOpen = false;

            inPad.Should().BeTrue();
            withPointer.Should().BeFalse();
        });
    }

    // A list box is not focusable itself; the focus sits on the row.
    private static void FocusRow(ListBox list, int index) =>
        list.ContainerFromIndex(index)!.Focus(NavigationMethod.Tab).Should().BeTrue();

    private static void RunPending() => PadRig.Drain();

    private static Popup ComboPopup(ComboBox combo) =>
        combo.GetVisualDescendants().OfType<Popup>().Single();
}
