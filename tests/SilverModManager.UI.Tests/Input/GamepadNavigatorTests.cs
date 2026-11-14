using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SilverModManager.App.UI.Input;
using SilverModManager.App.UI.Input.Bindings;
using SilverModManager.App.UI.Overlays;
using SilverModManager.Platform.Gamepad;
using SilverModManager.Sdk;
using SilverModManager.UI.Tests.Framework;

namespace SilverModManager.UI.Tests.Input;

// One keyboard focus and one set of key events for the whole headless platform.
[Collection(HeadlessUi.Collection)]
public class GamepadNavigatorTests(IServiceProvider provider) : AUiTest(provider)
{
    /// <summary>A window with four buttons in a 2x2 grid, the first one focused, and a navigator wired to a fake pad.</summary>
    private sealed class Rig : IDisposable
    {
        public readonly FakeTimeProvider Time = new();
        public readonly FakeGamepadSource Source = new();
        public readonly IOverlayController Overlays = Substitute.For<IOverlayController>();
        public readonly InputModeService Mode;
        public readonly KeyOwnershipFilter Ownership;
        public readonly GamepadNavigator Navigator;
        public readonly Window Window;
        public readonly Button[] Buttons = [new() { Content = "A" }, new() { Content = "B" }, new() { Content = "C" }, new() { Content = "D" }];
        public readonly int[] Clicks = new int[4];
        public readonly List<Key> Escapes = [];
        public bool Active = true;
        private readonly IDisposable _attachment;

        public Rig(Func<Rig, IEnumerable<IPadBinding>>? bindings = null)
        {
            var detector = Substitute.For<IGameModeDetector>();
            Mode = new InputModeService(detector);
            Ownership = new KeyOwnershipFilter(new GamepadPresence(Time), new GamepadPresence(Time));
            var grid = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,Auto"),
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto"),
            };
            XYFocus.SetNavigationModes(grid, XYFocusNavigationModes.Enabled);
            for (var i = 0; i < 4; i++)
            {
                var index = i;
                Grid.SetRow(Buttons[i], i / 2);
                Grid.SetColumn(Buttons[i], i % 2);
                Buttons[i].Click += (_, _) => Clicks[index]++;
                grid.Children.Add(Buttons[i]);
            }

            Window = new Window { Content = grid, Width = 400, Height = 300 };
            Window.AddHandler(InputElement.KeyDownEvent, (_, e) =>
            {
                if (e.Key == Key.Escape) Escapes.Add(e.Key);
            });
            Navigator = new GamepadNavigator(Source, Mode, Ownership, bindings?.Invoke(this) ?? DefaultBindings(), () => Active, Overlays);
            _attachment = Navigator.Attach(Window);
        }

        public IEnumerable<IPadBinding> DefaultBindings() => [new FocusMoveBinding(), new ActivateBinding(), new BackBinding()];

        public void Dispose()
        {
            _attachment.Dispose();
            Navigator.Dispose();
            Mode.PointerMoved();
            Window.Close();
        }
    }

    private static void Drain() => Dispatcher.UIThread.RunJobs();

    private static void Show(Rig rig)
    {
        ShowRendered(rig.Window);
        rig.Buttons[0].Focus(NavigationMethod.Tab);
        Drain();
    }

    [Fact]
    public async Task DPadRightMovesFocusToTheButtonOnTheRight()
    {
        await OnUi(() =>
        {
            using var rig = new Rig();
            Show(rig);

            rig.Source.Press(GamepadButton.DPadRight);
            Drain();

            rig.Buttons[1].IsFocused.Should().BeTrue();
        });
    }

    [Fact]
    public async Task DPadDownMovesFocusToTheButtonBelow()
    {
        await OnUi(() =>
        {
            using var rig = new Rig();
            Show(rig);

            rig.Source.Press(GamepadButton.DPadDown);
            Drain();

            rig.Buttons[2].IsFocused.Should().BeTrue();
        });
    }

    [Fact]
    public async Task AActivatesTheFocusedButton()
    {
        await OnUi(() =>
        {
            using var rig = new Rig();
            Show(rig);

            rig.Source.Press(GamepadButton.A);
            Drain();

            rig.Clicks.Should().Equal(1, 0, 0, 0);
        });
    }

    [Fact]
    public async Task NothingFiresWhileTheWindowIsInactive()
    {
        await OnUi(() =>
        {
            using var rig = new Rig { Active = false };
            Show(rig);

            rig.Source.Press(GamepadButton.A);
            Drain();

            rig.Clicks.Should().AllBeEquivalentTo(0);
            rig.Mode.Mode.Should().Be(InputMode.Pointer, "a pad pressed in another window does not take over this one");
        });
    }

    [Fact]
    public async Task BClosesTheCurrentOverlayBeforeSendingEscape()
    {
        await OnUi(() =>
        {
            using var rig = new Rig();
            var overlay = Substitute.For<IOverlayViewModel>();
            rig.Overlays.CurrentOverlay.Returns(overlay);
            Show(rig);

            rig.Source.Press(GamepadButton.B);
            Drain();

            rig.Overlays.Received(1).Remove(overlay);
            rig.Escapes.Should().BeEmpty();
        });
    }

    [Fact]
    public async Task BSendsEscapeWhenNoOverlayIsOpen()
    {
        await OnUi(() =>
        {
            using var rig = new Rig();
            rig.Overlays.CurrentOverlay.Returns((IOverlayViewModel?)null);
            Show(rig);

            rig.Source.Press(GamepadButton.B);
            Drain();

            rig.Escapes.Should().ContainSingle();
        });
    }

    [Fact]
    public async Task APadDPadWithin150msAfterAPhysicalArrowIsDropped()
    {
        await OnUi(() =>
        {
            using var rig = new Rig();
            Show(rig);
            rig.Window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            Drain();
            rig.Buttons[1].IsFocused.Should().BeTrue("the physical arrow moved focus right");

            rig.Time.Advance(TimeSpan.FromMilliseconds(100));
            rig.Source.Press(GamepadButton.DPadDown);
            Drain();
            rig.Buttons[1].IsFocused.Should().BeTrue("Steam Input's echo of the same press must not move focus twice");

            rig.Time.Advance(TimeSpan.FromMilliseconds(100));
            rig.Source.Press(GamepadButton.DPadDown);
            Drain();
            rig.Buttons[3].IsFocused.Should().BeTrue();
        });
    }

    [Fact]
    public async Task AKeyboardArrowRightAfterAPadEventDoesNotMoveFocus()
    {
        await OnUi(() =>
        {
            using var rig = new Rig();
            Show(rig);
            rig.Source.Press(GamepadButton.DPadDown);
            Drain();
            rig.Buttons[2].IsFocused.Should().BeTrue();

            rig.Time.Advance(TimeSpan.FromMilliseconds(100));
            rig.Window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            Drain();

            rig.Buttons[2].IsFocused.Should().BeTrue("the keyboard arrow is the pad's own echo");
        });
    }

    [Fact]
    public async Task APadEventSwitchesToPadModeAndAPointerMoveBack()
    {
        await OnUi(() =>
        {
            using var rig = new Rig();
            Show(rig);

            rig.Source.Press(GamepadButton.DPadRight);
            Drain();
            rig.Mode.Mode.Should().Be(InputMode.Pad);

            rig.Window.MouseMove(new Point(10, 10));
            rig.Window.MouseMove(new Point(30, 30));
            rig.Mode.Mode.Should().Be(InputMode.Pointer);
        });
    }

    [Fact]
    public async Task AStickAtRestOrAPadPluggedInDoesNotTakeOverTheKeyboard()
    {
        await OnUi(() =>
        {
            using var rig = new Rig();
            Show(rig);

            // A stick at rest still reports small readings now and then.
            rig.Source.Emit(new Stick(TimeSpan.Zero, StickSide.Left, 0.04f, -0.02f));
            rig.Source.Emit(new Stick(TimeSpan.Zero, StickSide.Right, -0.03f, 0.05f));
            rig.Source.Emit(new PadAdded(TimeSpan.Zero, "Xbox Controller", GamepadType.Xbox));
            Drain();
            rig.Mode.Mode.Should().Be(InputMode.Pointer);

            rig.Window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            Drain();
            rig.Buttons[1].IsFocused.Should().BeTrue("the arrow is not an echo of anything the pad did");
        });
    }

    [Fact]
    public async Task ADeflectedRightStickCountsAsPadUse()
    {
        await OnUi(() =>
        {
            using var rig = new Rig();
            Show(rig);

            rig.Source.Emit(new Stick(TimeSpan.Zero, StickSide.Right, 0f, 0.8f));
            Drain();

            rig.Mode.Mode.Should().Be(InputMode.Pad);
        });
    }

    [Fact]
    public async Task KeyboardTypedArrowsOnlyMoveFocusSpatiallyOnceAPadIsInUse()
    {
        await OnUi(() =>
        {
            using var rig = new Rig();
            Show(rig);
            XYFocus.GetNavigationModes(rig.Window).Should().Be(XYFocusNavigationModes.Gamepad);

            rig.Mode.PadUsed();
            XYFocus.GetNavigationModes(rig.Window).Should().Be(XYFocusNavigationModes.Enabled, "Steam Input turns the pad into keyboard arrows");

            rig.Mode.PointerMoved();
            XYFocus.GetNavigationModes(rig.Window).Should().Be(XYFocusNavigationModes.Gamepad);
        });
    }

    [Fact]
    public async Task APadWithNothingFocusedFocusesTheFirstControl()
    {
        await OnUi(() =>
        {
            using var rig = new Rig();
            ShowRendered(rig.Window);
            rig.Window.FocusManager!.ClearFocus();
            Drain();

            rig.Source.Press(GamepadButton.DPadRight);
            Drain();

            rig.Window.FocusManager!.GetFocusedElement().Should().NotBeNull();
        });
    }

    [Fact]
    public async Task OnlyTheFirstMatchingBindingRuns()
    {
        await OnUi(() =>
        {
            var first = Substitute.For<IPadBinding>();
            var second = Substitute.For<IPadBinding>();
            foreach (var binding in new[] { first, second })
            {
                binding.Matches(Arg.Any<GamepadEvent>(), Arg.Any<FocusContext>()).Returns(true);
                binding.CanExecute(Arg.Any<FocusContext>()).Returns(true);
            }

            using var rig = new Rig(_ => [first, second]);
            Show(rig);

            rig.Source.Press(GamepadButton.A);
            Drain();

            first.Received(1).Execute(Arg.Any<GamepadEvent>(), Arg.Any<FocusContext>());
            second.DidNotReceive().Execute(Arg.Any<GamepadEvent>(), Arg.Any<FocusContext>());
        });
    }

    [Fact]
    public async Task ABindingThatCannotExecuteIsSkipped()
    {
        await OnUi(() =>
        {
            var blocked = Substitute.For<IPadBinding>();
            blocked.Matches(Arg.Any<GamepadEvent>(), Arg.Any<FocusContext>()).Returns(true);
            blocked.CanExecute(Arg.Any<FocusContext>()).Returns(false);
            var fallback = Substitute.For<IPadBinding>();
            fallback.Matches(Arg.Any<GamepadEvent>(), Arg.Any<FocusContext>()).Returns(true);
            fallback.CanExecute(Arg.Any<FocusContext>()).Returns(true);

            using var rig = new Rig(_ => [blocked, fallback]);
            Show(rig);

            rig.Source.Press(GamepadButton.A);
            Drain();

            blocked.DidNotReceive().Execute(Arg.Any<GamepadEvent>(), Arg.Any<FocusContext>());
            fallback.Received(1).Execute(Arg.Any<GamepadEvent>(), Arg.Any<FocusContext>());
        });
    }

    [Fact]
    public async Task DeactivatingTheWindowPausesTheSourceAndActivatingResumesIt()
    {
        await OnUi(() =>
        {
            using var rig = new Rig();
            ShowRendered(rig.Window);

            rig.Navigator.WindowActivationChanged(false);
            rig.Source.Paused.Should().BeTrue();

            rig.Navigator.WindowActivationChanged(true);
            rig.Source.Paused.Should().BeFalse();
        });
    }

    [Fact]
    public async Task DisposingStopsListeningToThePad()
    {
        await OnUi(() =>
        {
            var counting = Substitute.For<IPadBinding>();
            using var rig = new Rig(_ => [counting]);
            Show(rig);
            rig.Navigator.Dispose();

            rig.Source.Press(GamepadButton.A);
            Drain();

            counting.DidNotReceiveWithAnyArgs().Matches(default!, default!);
        });
    }
}
