using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using R3;
using SilverModManager.App.UI.Overlays;
using SilverModManager.Platform.Gamepad;

namespace SilverModManager.App.UI.Input;

/// <summary>
/// Turns pad events into the key and context-menu events a keyboard user would raise, so every control that works with
/// a keyboard works with a pad. An event counts only while the window is active, and is dropped when a physical
/// arrow, Enter or Escape was just pressed (see <see cref="KeyOwnershipFilter"/>).
/// </summary>
public sealed class GamepadNavigator : IDisposable
{
    private readonly IGamepadSource _source;
    private readonly IInputModeService _mode;
    private readonly KeyOwnershipFilter _ownership;
    private readonly IReadOnlyList<IPadBinding> _bindings;
    private readonly Func<bool> _isWindowActive;
    private readonly IOverlayController _overlays;
    private readonly IDisposable _subscription;
    private readonly Subject<Unit> _contextChanged = new();
    private readonly IDisposable _modeSubscription;
    private TopLevel? _root;

    public GamepadNavigator(
        IGamepadSource source,
        IInputModeService mode,
        KeyOwnershipFilter ownership,
        IEnumerable<IPadBinding> bindings,
        Func<bool> isWindowActive,
        IOverlayController overlays)
    {
        _source = source;
        _mode = mode;
        _ownership = ownership;
        _bindings = bindings.ToList();
        _isWindowActive = isWindowActive;
        _overlays = overlays;
        // The source reports on its own thread; bindings touch controls.
        _subscription = source.Events.Subscribe(e => Dispatcher.UIThread.Post(() => Handle(e)));
        _modeSubscription = mode.Modes.Subscribe(_ =>
        {
            ApplyNavigationModes();
            _contextChanged.OnNext(Unit.Default);
        });
        if (overlays is INotifyPropertyChanged observable) observable.PropertyChanged += OnOverlaysChanged;
    }

    /// <summary>
    /// Fires when what the pad can do may have changed: the input mode, the focus or the open overlay. The hint bar redraws on it.
    /// </summary>
    public Observable<Unit> ContextChanged => _contextChanged;

    /// <summary>The kind of the first connected pad, which decides the button labels.</summary>
    public GamepadType PadType => _source.Pads.FirstOrDefault()?.Type ?? GamepadType.Unknown;

    /// <summary>The window the navigator drives, once attached.</summary>
    public TopLevel? Root => _root;

    /// <summary>The bindings, in the order they are tried.</summary>
    public IReadOnlyList<IPadBinding> Bindings => _bindings;

    /// <summary>The input mode the navigator keeps up to date.</summary>
    public IInputModeService Mode => _mode;

    /// <summary>
    /// Builds the context the bindings act on, or null when no window is attached. The hint bar asks it which bindings apply.
    /// </summary>
    public FocusContext? CurrentContext() =>
        _root is { } root ? new FocusContext(root, root.FocusManager?.GetFocusedElement(), _overlays) : null;

    /// <summary>
    /// Starts watching the window: its tunnelling key presses go through the ownership filter, pointer movement switches
    /// the input mode back, and the source is paused whenever the window is not active.
    /// </summary>
    public IDisposable Attach(TopLevel root)
    {
        _root = root;
        ApplyNavigationModes();
        root.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        root.AddHandler(InputElement.GotFocusEvent, OnGotFocus, RoutingStrategies.Bubble, handledEventsToo: true);
        root.PointerMoved += OnPointerMoved;
        if (root is Window window)
        {
            window.Activated += OnActivated;
            window.Deactivated += OnDeactivated;
        }

        return Disposable.Create(() =>
        {
            root.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            root.RemoveHandler(InputElement.GotFocusEvent, OnGotFocus);
            root.PointerMoved -= OnPointerMoved;
            if (root is Window w)
            {
                w.Activated -= OnActivated;
                w.Deactivated -= OnDeactivated;
            }
            if (ReferenceEquals(_root, root)) _root = null;
        });
    }

    /// <summary>Pauses the pad while the window is in the background, so a pad used in a game or another window does nothing here.</summary>
    public void WindowActivationChanged(bool active)
    {
        _source.Pause(!active);
        if (active) return;
        foreach (var binding in _bindings) binding.WindowDeactivated();
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _modeSubscription.Dispose();
        if (_overlays is INotifyPropertyChanged observable) observable.PropertyChanged -= OnOverlaysChanged;
        _contextChanged.Dispose();
    }

    /// <summary>
    /// A pad drives focus through <see cref="XYFocus"/>, which is off for keyboards by default. Where Steam Input turns the pad
    /// into arrow keys (Game Mode) or the user is on the pad, the keyboard-typed arrows need it too.
    /// </summary>
    private void ApplyNavigationModes()
    {
        if (_root is null) return;
        XYFocus.SetNavigationModes(_root, _mode.UsesOverlayPopups ? XYFocusNavigationModes.Enabled : XYFocusNavigationModes.Gamepad);
    }

    private void OnGotFocus(object? sender, RoutedEventArgs e) => _contextChanged.OnNext(Unit.Default);

    private void OnOverlaysChanged(object? sender, PropertyChangedEventArgs e) => _contextChanged.OnNext(Unit.Default);

    private void OnKeyDown(object? sender, KeyEventArgs e) => _ownership.OnKeyDown(e);

    private void OnPointerMoved(object? sender, PointerEventArgs e) => _mode.PointerMoved();

    private void OnActivated(object? sender, EventArgs e) => WindowActivationChanged(true);

    private void OnDeactivated(object? sender, EventArgs e) => WindowActivationChanged(false);

    private static bool IsOwnedByKeys(GamepadEvent e) => e switch
    {
        ButtonDown d => IsKeyEchoed(d.Button),
        ButtonRepeat r => IsKeyEchoed(r.Button),
        _ => false,
    };

    private static bool IsKeyEchoed(GamepadButton button) =>
        button is GamepadButton.DPadUp or GamepadButton.DPadDown or GamepadButton.DPadLeft or GamepadButton.DPadRight or GamepadButton.A or GamepadButton.B;

    private static bool IsDeliberate(GamepadEvent e) => e switch
    {
        ButtonDown or ButtonRepeat => true,
        Stick stick => stick.IsDeflected,
        _ => false,
    };

    internal void Handle(GamepadEvent e)
    {
        if (!_isWindowActive() || CurrentContext() is not { } context) return;

        // Dropped before it counts as pad use: a pad that lost the race must not make the keyboard's own repeats look like its echo.
        if (IsOwnedByKeys(e) && _ownership.KeyRecent) return;

        // A release, a resting stick's noise or a pad being plugged in is nobody using it. Counting them would switch the
        // window to Pad mode and have the ownership filter swallow the keyboard's arrows. The event still reaches the
        // bindings: a stick coming back to the centre is what stops the scroll.
        if (IsDeliberate(e))
        {
            _ownership.PadEvent();
            _mode.PadUsed();
        }

        foreach (var binding in _bindings)
        {
            if (!binding.Matches(e, context) || !binding.CanExecute(context)) continue;
            binding.Execute(e, context);
            return;
        }
    }
}
