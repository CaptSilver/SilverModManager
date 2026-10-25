using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace SilverModManager.App.UI.Controls.Cards;

/// <summary>
/// A card thumbnail that loads itself while it is on screen. A virtualized list attaches only the rows it shows, so
/// attaching is the moment to fetch and detaching is the moment to give up.
/// </summary>
public sealed class CardThumbnail : Image
{
    public static readonly StyledProperty<Uri?> ImageUrlProperty = AvaloniaProperty.Register<CardThumbnail, Uri?>(nameof(ImageUrl));

    private CancellationTokenSource? _loading;

    public Uri? ImageUrl
    {
        get => GetValue(ImageUrlProperty);
        set => SetValue(ImageUrlProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(Image);

    static CardThumbnail()
    {
        ImageUrlProperty.Changed.AddClassHandler<CardThumbnail>((icon, _) =>
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

        var loader = this.FindAncestorOfType<CardGrid>()?.Thumbnails;
        if (loader is null) return;

        var url = ImageUrl;
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
            // A recycled row may have been given another entry while this one was in flight.
            if (!cts.IsCancellationRequested) Source = image;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
