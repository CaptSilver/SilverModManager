using Avalonia.Controls;
using Avalonia.Input;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SilverModManager.App.UI.Input;
using SilverModManager.App.UI.Input.Bindings;
using SilverModManager.App.UI.Input.Reorder;
using SilverModManager.Platform.Gamepad;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Tests.Framework;

namespace SilverModManager.UI.Tests.Input.Reorder;

[Collection(HeadlessUi.Collection)]
public class ReorderBindingTests(IServiceProvider provider) : AUiTest(provider)
{
    /// <summary>Ten rows "row0".."row9" listed in order; every move is recorded and applied to <see cref="Index"/>.</summary>
    private sealed class FakeRows : IReorderable<string>
    {
        public readonly List<(string Item, int Delta)> Moves = [];
        public int Index;
        public Exception? FailWith;

        public int CountFor(string item) => 10;

        public int IndexOf(string item) => Index;

        public Task MoveBy(string item, int delta, CancellationToken token)
        {
            Moves.Add((item, delta));
            if (FailWith is not null) return Task.FromException(FailWith);
            Index = Math.Clamp(Index + delta, 0, 9);
            return Task.CompletedTask;
        }
    }

    private sealed class Page : IDisposable
    {
        public readonly FakeRows Rows = new();
        public readonly IWindowNotificationService Toasts = Substitute.For<IWindowNotificationService>();
        public readonly FakeTimeProvider Time = new();
        public readonly ReorderState State;
        public readonly Button First = new() { Content = "first", DataContext = "row0" };
        public readonly PadRig Rig;

        public Page(bool withSource = true)
        {
            State = new ReorderState(Toasts, NullLogger<ReorderState>.Instance);
            var panel = new StackPanel { Children = { First, new Button { Content = "second", DataContext = "row1" } } };
            if (withSource) ReorderSources.SetSource(panel, new ReorderSource<string>(Rows, InRow, Time));
            Rig = new PadRig(panel, () => Bindings(State));
            Rig.Show();
            First.Focus(NavigationMethod.Tab);
        }

        private static bool InRow(IInputElement focused, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Control? row)
        {
            if (focused is Button { DataContext: string name } button)
            {
                key = name;
                row = button;
                return true;
            }

            key = null;
            row = null;
            return false;
        }

        public static IEnumerable<IPadBinding> Bindings(ReorderState state) =>
        [
            new ReorderBinding(ReorderStep.Drop, state),
            new ReorderBinding(ReorderStep.Cancel, state),
            new ReorderBinding(ReorderStep.Up, state),
            new ReorderBinding(ReorderStep.Down, state),
            new ReorderBinding(ReorderStep.PageUp, state),
            new ReorderBinding(ReorderStep.PageDown, state),
            new ReorderBinding(ReorderStep.PickUp, state),
            new ReorderGuardBinding(state),
            new ContextMenuBinding(),
            new FocusMoveBinding(),
            new ActivateBinding(),
        ];

        public void Press(GamepadButton button) => Rig.Send(new ButtonDown(TimeSpan.Zero, button));

        public void Dispose() => Rig.Dispose();
    }

    [Fact]
    public async Task YThenDownTwiceThenAMovesTheRowDownByTwo()
    {
        await OnUi(() =>
        {
            using var page = new Page();

            page.Press(GamepadButton.Y);
            page.Press(GamepadButton.DPadDown);
            page.Press(GamepadButton.DPadDown);
            page.Press(GamepadButton.A);

            page.Rows.Moves.Should().Equal(("row0", 2));
            page.State.IsLive.Should().BeFalse();
        });
    }

    [Fact]
    public async Task TheLiftedRowIsMarkedWhileItIsHeldAndTheHintBarNamesTheNewKeys()
    {
        await OnUi(() =>
        {
            using var page = new Page();
            page.Press(GamepadButton.Y);
            page.Press(GamepadButton.DPadDown);

            page.First.Classes.Should().Contain("Lifted");
            var hints = HintBarItems.Build(page.Rig.Navigator.Bindings, page.Rig.Navigator.CurrentContext()!, GamepadType.Xbox);
            hints.Select(h => h.Label).Should().Contain(["Move (position 2 of 10)", "Drop", "Put back", "Move a page"]);

            page.Press(GamepadButton.A);
            page.First.Classes.Should().NotContain("Lifted");
        });
    }

    [Fact]
    public async Task BDuringASessionPutsTheRowBack()
    {
        await OnUi(() =>
        {
            using var page = new Page();
            page.Press(GamepadButton.Y);
            page.Press(GamepadButton.DPadDown);
            page.Press(GamepadButton.DPadDown);
            page.Time.Advance(TimeSpan.FromSeconds(1));
            PadRig.Drain();
            page.Rows.Moves.Should().Equal(("row0", 2));

            page.Press(GamepadButton.B);

            page.Rows.Moves.Should().Equal(("row0", 2), ("row0", -2));
            page.State.IsLive.Should().BeFalse();
            page.First.Classes.Should().NotContain("Lifted");
        });
    }

    [Fact]
    public async Task RtNudgesByAPage()
    {
        await OnUi(() =>
        {
            using var page = new Page();
            page.Press(GamepadButton.Y);

            page.Press(GamepadButton.RT);
            page.Press(GamepadButton.A);

            // No scroll viewer to measure, so a page is the fallback of eight rows.
            page.Rows.Moves.Should().Equal(("row0", 8));
        });
    }

    [Fact]
    public async Task AFailedMoveShowsTheToastWithTheRealPosition()
    {
        await OnUi(() =>
        {
            using var page = new Page();
            page.Rows.FailWith = new InvalidOperationException("the order changed");
            page.Press(GamepadButton.Y);
            page.Press(GamepadButton.DPadDown);

            page.Press(GamepadButton.A);

            page.Toasts.Received(1).ShowToast("Could not move it; it is at position 1", ToastNotificationVariant.Failure);
            page.State.IsLive.Should().BeFalse();
            page.First.Classes.Should().NotContain("Lifted");
        });
    }

    [Fact]
    public async Task OtherButtonsDoNothingWhileARowIsLifted()
    {
        await OnUi(() =>
        {
            using var page = new Page();
            page.Press(GamepadButton.Y);
            page.Press(GamepadButton.Menu);
            page.Press(GamepadButton.X);
            page.Press(GamepadButton.LB);

            page.State.IsLive.Should().BeTrue();
            page.Rig.Focused.Should().BeSameAs(page.First);
        });
    }

    [Fact]
    public async Task APageThatDoesNotReorderLeavesYAlone()
    {
        await OnUi(() =>
        {
            using var page = new Page(withSource: false);

            page.Press(GamepadButton.Y);

            page.State.IsLive.Should().BeFalse();
            page.Rows.Moves.Should().BeEmpty();
        });
    }

    [Fact]
    public async Task TheWindowLosingActivationPutsALiftedRowBack()
    {
        await OnUi(() =>
        {
            using var page = new Page();
            page.Press(GamepadButton.Y);
            page.Press(GamepadButton.DPadDown);
            page.Time.Advance(TimeSpan.FromSeconds(1));
            PadRig.Drain();

            page.Rig.Navigator.WindowActivationChanged(false);
            PadRig.Drain();

            page.State.IsLive.Should().BeFalse();
            page.Rows.Moves.Should().Equal(("row0", 1), ("row0", -1));
        });
    }
}
