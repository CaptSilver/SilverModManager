namespace NexusMods.Games.FromSoft.Overlay;

/// <summary>
/// Rewrites one file of the configuration layer on its way into the overlay's upper folder, for a value that must not
/// be stored in the loadout, such as a password. Files no transform applies to are copied as they are.
/// </summary>
public interface IConfigurationLayerTransform
{
    /// <param name="relativePath">The file's path inside the layer, with forward slashes.</param>
    bool AppliesTo(string relativePath);

    /// <summary>Writes the transformed copy of <paramref name="sourceFile"/> to <paramref name="targetFile"/>, replacing what is there.</summary>
    Task WriteAsync(string sourceFile, string targetFile, CancellationToken cancellationToken);
}
