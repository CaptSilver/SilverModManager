using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using R3;
using SilverModManager.Platform.Gamepad;

namespace SilverModManager.App.UI.Input;

/// <summary>
/// A strip along the bottom of the window naming what each pad button does right now. It shows only while a pad is the
/// active input, and lists only the buttons that have something to act on.
/// </summary>
public sealed class HintBar : Border
{
    private readonly StackPanel _items = new() { Orientation = Orientation.Horizontal, Spacing = 24, HorizontalAlignment = HorizontalAlignment.Center };
    private GamepadNavigator? _navigator;

    public HintBar()
    {
        IsVisible = false;
        Padding = new Avalonia.Thickness(16, 8);
        Child = _items;
        Classes.Add("HintBar");
    }

    /// <summary>Starts following the navigator. The bar redraws whenever the mode, the focus or the open overlay changes.</summary>
    public IDisposable Attach(GamepadNavigator navigator)
    {
        _navigator = navigator;
        Refresh();
        var subscription = navigator.ContextChanged.Subscribe(_ => Dispatcher.UIThread.Post(Refresh));
        return Disposable.Create(() =>
        {
            subscription.Dispose();
            if (ReferenceEquals(_navigator, navigator)) _navigator = null;
        });
    }

    internal void Refresh()
    {
        if (_navigator is not { } navigator || navigator.CurrentContext() is not { } context)
        {
            IsVisible = false;
            return;
        }

        IsVisible = navigator.Mode.Mode == InputMode.Pad;
        _items.Children.Clear();
        if (!IsVisible) return;

        foreach (var item in HintBarItems.Build(navigator.Bindings, context, navigator.PadType))
        {
            var entry = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            entry.Children.Add(new Border
            {
                CornerRadius = new Avalonia.CornerRadius(4),
                Padding = new Avalonia.Thickness(8, 2),
                BorderThickness = new Avalonia.Thickness(1),
                BorderBrush = Brushes.Gray,
                Child = new TextBlock { Text = item.Glyph, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
            });
            entry.Children.Add(new TextBlock { Text = item.Label, VerticalAlignment = VerticalAlignment.Center });
            _items.Children.Add(entry);
        }
    }
}
