using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Threading;
using SilverModManager.App.UI.Settings;
using SilverModManager.Sdk.Settings;
using R3;

namespace SilverModManager.App.UI.Windows;

/// <summary>
/// The Glass setting applied to windows: the desktop shows through the window frame, blurred by the compositor.
/// Blur strength is the compositor's own (KWin: Desktop Effects > Blur); the app only chooses how much of its
/// colour covers it. A window only goes translucent once the compositor reports an actual blur, otherwise
/// it stays on the opaque surface colour, so the look never depends on a compositor that can't deliver.
/// </summary>
public sealed class GlassAppearance : IDisposable
{
    public const double MinTint = 0.2;
    public const double MaxTint = 0.8;
    public const double DefaultTint = 0.4;

    private readonly IDisposable _subscription;

    /// <summary>
    /// Whether the desktop named by XDG_CURRENT_DESKTOP (colon-separated, any case) can blur the window. Avalonia's
    /// X11 backend only grants blur under KWin and keeps that check internal, so this mirrors it by desktop name:
    /// a KDE Plasma session sets XDG_CURRENT_DESKTOP=KDE.
    /// </summary>
    public static bool IsBlurAvailableOn(string? xdgCurrentDesktop) =>
        xdgCurrentDesktop is not null
        && xdgCurrentDesktop.Split(':').Any(name => name.Equals("KDE", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The instance windows that are created without a view model (dialogs, installers) attach to. Set when the
    /// app's instance is created; null in a host that never showed the main window, which leaves those windows opaque.
    /// </summary>
    public static GlassAppearance? Shared { get; private set; }

    public bool Enabled { get; private set; }

    public double Tint { get; private set; } = DefaultTint;

    public IReadOnlyList<WindowTransparencyLevel> TransparencyLevelHint => HintFor(Enabled);

    /// <summary>Raised after <see cref="Enabled"/> or <see cref="Tint"/> changed. May fire on any thread.</summary>
    public event Action? Changed;

    public GlassAppearance(ISettingsManager settingsManager)
    {
        Apply(settingsManager.Get<AppearanceSettings>());
        _subscription = settingsManager.GetChanges<AppearanceSettings>(prependCurrent: false).Subscribe(settings =>
        {
            Apply(settings);
            Changed?.Invoke();
        });
        Shared = this;
    }

    private void Apply(AppearanceSettings settings)
    {
        Enabled = settings.GlassEnabled;
        Tint = ClampTint(settings.GlassTint);
    }

    /// <summary>Keeps a stored tint inside the slider's range, so a hand-edited file can't make the chrome unreadable.</summary>
    public static double ClampTint(double tint) => double.IsNaN(tint) ? DefaultTint : Math.Clamp(tint, MinTint, MaxTint);

    public static byte TintAlpha(double tint) => (byte)Math.Round(ClampTint(tint) * 255);

    public static IReadOnlyList<WindowTransparencyLevel> HintFor(bool enabled) => enabled
        ? [WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent]
        : [WindowTransparencyLevel.None];

    /// <summary>
    /// Plain Transparent counts as no blur: the desktop would show through sharp and the text on the chrome
    /// would sit on whatever is behind it.
    /// </summary>
    public static bool IsBlurred(WindowTransparencyLevel level) =>
        level == WindowTransparencyLevel.Blur || level == WindowTransparencyLevel.AcrylicBlur;

    /// <summary>
    /// The translucent background for a blurred window, or null when the window should keep its own opaque one.
    /// </summary>
    public static IBrush? TintedBackgroundFor(bool enabled, WindowTransparencyLevel actual, double tint, Color surface)
    {
        if (!enabled || !IsBlurred(actual)) return null;
        return new SolidColorBrush(Color.FromArgb(TintAlpha(tint), surface.R, surface.G, surface.B));
    }

    /// <summary>
    /// How opaque a raised panel is over the blurred desktop: halfway between the window's tint and solid, so the
    /// gutters around it stay the most see-through part and text on the panel keeps most of its backing.
    /// </summary>
    public static double PanelTint(double tint)
    {
        var frame = ClampTint(tint);
        return frame + (1 - frame) / 2;
    }

    /// <summary>The translucent background for a panel inside a blurred window, or null when it keeps its own opaque one.</summary>
    public static IBrush? PanelBackgroundFor(bool enabled, WindowTransparencyLevel actual, double tint, Color surface)
    {
        if (!enabled || !IsBlurred(actual)) return null;
        var alpha = (byte)Math.Round(PanelTint(tint) * 255);
        return new SolidColorBrush(Color.FromArgb(alpha, surface.R, surface.G, surface.B));
    }

    private static Color SurfaceColor(string key) =>
        (Application.Current?.TryFindResource(key, out var resource) ?? false) && resource is Color surface
            ? surface
            : throw new InvalidOperationException($"The theme has no colour resource `{key}` to use as a background");

    /// <summary>
    /// Keeps <paramref name="window"/>'s transparency hint and background in step with the setting and with
    /// what the compositor actually grants. <paramref name="surfaceKey"/> names the theme colour (for example
    /// "SurfaceBase") that the tint is made from; it should match the window's opaque background.
    /// </summary>
    public IDisposable Attach(Window window, string surfaceKey) => Attach(window, surfaceKey, panelSurfaceKey: null);

    /// <summary>
    /// As <see cref="Attach(Window, string)"/>, and also tints <paramref name="panels"/>, which are painted in the
    /// theme colour <paramref name="panelSurfaceKey"/>, with the more opaque <see cref="PanelTint"/>.
    /// </summary>
    public IDisposable Attach(Window window, string surfaceKey, string? panelSurfaceKey, params Border[] panels)
    {
        var surface = SurfaceColor(surfaceKey);
        var panelSurface = panelSurfaceKey is null ? default : SurfaceColor(panelSurfaceKey);

        // Each tint sits above the control's own Background (set in its axaml, a style or the theme) and is
        // removed again without blur, so the opaque look is exactly what the window had before Glass existed.
        var tints = new List<IDisposable>();

        void Refresh()
        {
            window.TransparencyLevelHint = TransparencyLevelHint;
            foreach (var tint in tints) tint.Dispose();
            tints.Clear();

            var actual = window.ActualTransparencyLevel;
            if (TintedBackgroundFor(Enabled, actual, Tint, surface) is { } brush
                && window.SetValue(TemplatedControl.BackgroundProperty, brush, BindingPriority.Animation) is { } windowTint)
                tints.Add(windowTint);
            foreach (var panel in panels)
            {
                if (PanelBackgroundFor(Enabled, actual, Tint, panelSurface) is { } panelBrush
                    && panel.SetValue(Border.BackgroundProperty, panelBrush, BindingPriority.Animation) is { } panelTint)
                    tints.Add(panelTint);
            }
        }

        void OnSettingChanged()
        {
            if (Dispatcher.UIThread.CheckAccess()) Refresh();
            else Dispatcher.UIThread.Post(Refresh);
        }

        void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property == TopLevel.ActualTransparencyLevelProperty) Refresh();
        }

        Refresh();
        Changed += OnSettingChanged;
        window.PropertyChanged += OnPropertyChanged;

        return new Detach(() =>
        {
            Changed -= OnSettingChanged;
            window.PropertyChanged -= OnPropertyChanged;
            foreach (var tint in tints) tint.Dispose();
            tints.Clear();
        });
    }

    /// <summary>Attaches to <see cref="Shared"/> for the life of the window; a no-op only when no instance exists (design time, headless hosts).</summary>
    public static void AttachShared(Window window, string surfaceKey)
    {
        var glass = Shared;
        if (glass is null) return;
        var attachment = glass.Attach(window, surfaceKey);
        window.Closed += (_, _) => attachment.Dispose();
    }

    public void Dispose()
    {
        _subscription.Dispose();
        if (ReferenceEquals(Shared, this)) Shared = null;
    }

    private sealed class Detach(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
