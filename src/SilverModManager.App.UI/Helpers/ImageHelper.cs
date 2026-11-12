using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.App.UI.Helpers;

/// <summary>
/// Shared utility for loading game images and creating IconValues.
/// </summary>
public static class ImageHelper
{
    /// <summary>
    /// Loads a game's icon, decoded to <paramref name="width"/>, or null when there is no game or its icon can't be read.
    /// </summary>
    public static ValueTask<Bitmap?> LoadGameIconAsync(IGameData? game, int width, ILogger logger)
        => game is null ? ValueTask.FromResult<Bitmap?>(null) : LoadAsync(game.IconImage, game.DisplayName, width, logger);

    /// <summary>
    /// Loads a game's tile, decoded to <paramref name="width"/>, or null when there is no game or its tile can't be read.
    /// </summary>
    public static ValueTask<Bitmap?> LoadGameTileAsync(IGameData? game, int width, ILogger logger)
        => game is null ? ValueTask.FromResult<Bitmap?>(null) : LoadAsync(game.TileImage, game.DisplayName, width, logger);

    private static async ValueTask<Bitmap?> LoadAsync(IStreamFactory image, string gameName, int width, ILogger logger)
    {
        try
        {
            await using var stream = await image.GetStreamAsync();
            return Bitmap.DecodeToWidth(stream, width);
        }
        catch (Exception ex)
        {
            // The card shows without the picture; an Error-level entry would be shown as a dialog.
            logger.LogWarning(ex, "While loading game image for {GameName}", gameName);
            return null;
        }
    }

    /// <summary>
    /// Creates an IconValue from a Bitmap with fallback handling.
    /// </summary>
    /// <param name="bitmap">The bitmap to convert to an IconValue.</param>
    /// <param name="fallback">The fallback IconValue to use if bitmap is null.</param>
    /// <returns>An IconValue containing the bitmap or the fallback.</returns>
    public static IconValue CreateIconValueFromBitmap(Bitmap? bitmap, IconValue fallback)
    {
        if (bitmap != null)
            return new AvaloniaImage(bitmap);
        
        return fallback;
    }
}
