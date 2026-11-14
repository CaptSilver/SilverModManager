using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.App.UI.Input;
using SilverModManager.App.UI.Input.Bindings;
using SilverModManager.App.UI.Overlays;
using SilverModManager.App.UI.Settings;
using SilverModManager.Platform.Gamepad;
using SilverModManager.UI.Tests.Framework;

namespace SilverModManager.UI.Tests.Input;

[Collection(HeadlessUi.Collection)]
public class HintBarTests(IServiceProvider provider) : AUiTest(provider)
{
    private static IPadBinding Fixed(GamepadButton button, string label, bool canExecute)
    {
        var binding = Substitute.For<IPadBinding>();
        binding.Button.Returns(button);
        binding.Label.Returns(label);
        binding.Hint(Arg.Any<FocusContext>()).Returns(label);
        binding.CanExecute(Arg.Any<FocusContext>()).Returns(canExecute);
        return binding;
    }

    private static FocusContext Context(IOverlayController overlays) => new(new Window(), null, overlays);

    [Fact]
    public async Task TheHintBarListsOnlyBindingsThatCanExecute()
    {
        await OnUi(() =>
        {
            var overlays = Substitute.For<IOverlayController>();
            overlays.CurrentOverlay.Returns(Substitute.For<IOverlayViewModel>());
            var context = Context(overlays);

            var items = HintBarItems.Build([new BackBinding(), Fixed(GamepadButton.Y, "Reorder", canExecute: false)], context, GamepadType.Xbox);

            items.Should().ContainSingle().Which.Should().Be(new HintItem("B", "Close"), "an open overlay turns Back into Close");
        });
    }

    [Fact]
    public async Task WithoutAnOverlayBIsBack()
    {
        await OnUi(() =>
        {
            var overlays = Substitute.For<IOverlayController>();
            overlays.CurrentOverlay.Returns((IOverlayViewModel?)null);

            HintBarItems.Build([new BackBinding()], Context(overlays), GamepadType.Xbox).Should().ContainSingle().Which.Label.Should().Be("Back");
        });
    }

    [Fact]
    public async Task TheGlyphFollowsThePadType()
    {
        await OnUi(() =>
        {
            var overlays = Substitute.For<IOverlayController>();

            HintBarItems.Build([new ActivateBinding(), Fixed(GamepadButton.A, "Select", true)], Context(overlays), GamepadType.PlayStation)
                .Select(i => i.Glyph).Should().Equal("Cross");
        });
    }

    [Fact]
    public async Task OneEntryPerButtonTheFirstBindingThatCanRunWins()
    {
        await OnUi(() =>
        {
            var overlays = Substitute.For<IOverlayController>();
            var blocked = Fixed(GamepadButton.RB, "Next loadout", canExecute: false);
            var first = Fixed(GamepadButton.RB, "Next tab", canExecute: true);
            var second = Fixed(GamepadButton.RB, "Other", canExecute: true);

            HintBarItems.Build([blocked, first, second], Context(overlays), GamepadType.Xbox)
                .Should().ContainSingle().Which.Label.Should().Be("Next tab");
        });
    }

    [Fact]
    public async Task TheDirectionsAreOneEntry()
    {
        await OnUi(() =>
        {
            var overlays = Substitute.For<IOverlayController>();

            HintBarItems.Build([new FocusMoveBinding()], Context(overlays), GamepadType.Xbox)
                .Should().ContainSingle().Which.Should().Be(new HintItem("D-pad", "Move"));
        });
    }

    [Fact]
    public async Task TheHintBarIsHiddenInPointerMode()
    {
        await OnUi(() =>
        {
            using var rig = new PadRig(new Button(), () => [new ActivateBinding()]);
            var bar = new HintBar();
            using var attachment = bar.Attach(rig.Navigator);
            rig.Show();

            bar.Refresh();
            bar.IsVisible.Should().BeFalse();

            rig.Mode.PadUsed();
            PadRig.Drain();
            bar.IsVisible.Should().BeTrue("the bar follows the input mode without being asked");

            rig.Mode.PointerMoved();
            PadRig.Drain();
            bar.IsVisible.Should().BeFalse();
        });
    }

    [Fact]
    public async Task TheBarRedrawsWhenTheFocusMoves()
    {
        await OnUi(() =>
        {
            var focusable = new Button { Content = "x" };
            var binding = new ActivateBinding();
            using var rig = new PadRig(focusable, () => [binding]);
            var bar = new HintBar();
            using var attachment = bar.Attach(rig.Navigator);
            rig.Show();
            rig.Window.FocusManager!.ClearFocus();
            rig.Mode.PadUsed();
            PadRig.Drain();
            bar.GetVisualDescendants().OfType<TextBlock>().Should().BeEmpty("Select needs something focused");

            focusable.Focus(NavigationMethod.Tab);
            PadRig.Drain();

            bar.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Should().Contain("Select");
        });
    }

    [Fact]
    public void ControllerOffGivesTheNullSourceWithoutASettingsReason()
    {
        var source = new NullGamepadSource(NullGamepadSource.ControllerOffReason);

        GameModeSettings.DisabledReasonFor(source).Should().BeNull("turning the pad off is the user's choice, not a defect");
    }

    [Fact]
    public void AnSdlLoadFailureShowsAsTheSettingsDisabledReason()
    {
        var source = new NullGamepadSource("libSDL3 could not be loaded: not found");

        GameModeSettings.DisabledReasonFor(source).Should().Contain("libSDL3");
    }

    [Fact]
    public void AWorkingSourceHasNoDisabledReason()
    {
        GameModeSettings.DisabledReasonFor(Substitute.For<IGamepadSource>()).Should().BeNull();
    }

    [Fact]
    public void ControllerIsOnByDefault()
    {
        new GameModeSettings().Controller.Should().BeTrue();
    }
}
