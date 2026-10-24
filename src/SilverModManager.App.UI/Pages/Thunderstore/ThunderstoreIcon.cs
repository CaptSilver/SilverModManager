using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace SilverModManager.App.UI.Pages.Thunderstore;

/// <summary>
/// A package icon that loads itself while it is on screen. A virtualized list attaches only the rows it shows, so
/// attaching is the moment to fetch and detaching is the moment to give up.
/// </summary>
public sealed class ThunderstoreIcon : Image
{
    public static readonly StyledProperty<Uri?> IconUrlProperty = AvaloniaProperty.Register<ThunderstoreIcon, Uri?>(nameof(IconUrl));

    private CancellationTokenSource? _loading;

    public Uri? IconUrl
    {
        get => GetValue(IconUrlProperty);
        set => SetValue(IconUrlProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(Image);

    static ThunderstoreIcon()
    {
        IconUrlProperty.Changed.AddClassHandler<ThunderstoreIcon>((icon, _) =>
        {
            if (icon.IsAttachedToVisualTree()) icon.Load();
        });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Load();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Cancel();
        base.OnDetachedFromVisualTree(e);
    }

    private void Cancel()
    {
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = null;
    }

    private async void Load()
    {
        Cancel();

        var loader = this.FindAncestorOfType<ThunderstoreView>()?.ViewModel?.Icons;
        if (loader is null) return;

        var url = IconUrl;
        if (url is null)
        {
            Source = loader.Placeholder;
            return;
        }

        var cached = loader.TryGetCached(url);
        Source = cached ?? loader.Placeholder;
        if (cached is not null) return;

        var cts = _loading = new CancellationTokenSource();
        try
        {
            var image = await loader.LoadAsync(url, cts.Token);
            // A recycled row may have been given another package while this one was in flight.
            if (!cts.IsCancellationRequested) Source = image;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
