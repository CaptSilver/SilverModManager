namespace SilverModManager.App.UI.Controls.Cards;

/// <summary>Stands in for the thumbnail loader in the previewer: every card shows the placeholder, nothing is fetched.</summary>
internal sealed class DesignThumbnailLoader : IThumbnailLoader
{
    public Avalonia.Media.IImage Placeholder => ImagePipelines.ModPageThumbnailFallback;
    public Avalonia.Media.IImage? TryGetCached(Uri url) => null;
    public Task<Avalonia.Media.IImage> LoadAsync(Uri url, CancellationToken cancellationToken) => Task.FromResult(Placeholder);
}
