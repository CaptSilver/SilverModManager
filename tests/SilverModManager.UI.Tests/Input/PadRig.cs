using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SilverModManager.App.UI.Input;
using SilverModManager.App.UI.Overlays;
using SilverModManager.Sdk;
using SilverModManager.UI.Tests.Framework;

namespace SilverModManager.UI.Tests.Input;

/// <summary>A window with the given content and a navigator wired to a fake pad. Must be built and used on the UI thread.</summary>
internal sealed class PadRig : IDisposable
{
    public readonly FakeTimeProvider Time = new();
    public readonly FakeGamepadSource Source = new();
    public readonly IOverlayController Overlays = Substitute.For<IOverlayController>();
    public readonly InputModeService Mode;
    public readonly GamepadNavigator Navigator;
    public readonly Window Window;
    private readonly IDisposable _attachment;

    public PadRig(Control content, Func<IEnumerable<IPadBinding>> bindings)
    {
        Mode = new InputModeService(Substitute.For<IGameModeDetector>());
        var ownership = new KeyOwnershipFilter(new SilverModManager.Platform.Gamepad.GamepadPresence(Time), new SilverModManager.Platform.Gamepad.GamepadPresence(Time));
        Overlays.CurrentOverlay.Returns((IOverlayViewModel?)null);
        Window = new Window { Content = content, Width = 600, Height = 400 };
        Navigator = new GamepadNavigator(Source, Mode, ownership, bindings(), () => true, Overlays);
        _attachment = Navigator.Attach(Window);
    }

    public IInputElement? Focused => Window.FocusManager?.GetFocusedElement();

    public void Show()
    {
        Window.Show();
        Window.UpdateLayout();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Drain();
    }

    public static void Drain() => Dispatcher.UIThread.RunJobs();

    /// <summary>Sends the pad event and runs the jobs it queued.</summary>
    public void Send(SilverModManager.Platform.Gamepad.GamepadEvent e)
    {
        Source.Emit(e);
        Drain();
    }

    public void Dispose()
    {
        _attachment.Dispose();
        Navigator.Dispose();
        Mode.PointerMoved();
        Window.Close();
    }
}
