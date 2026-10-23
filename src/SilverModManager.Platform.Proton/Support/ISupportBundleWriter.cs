using NexusMods.Paths;

namespace SilverModManager.Platform.Proton.Support;

/// <summary>
/// Collects what is needed to diagnose a problem report into one file the user can attach.
/// </summary>
public interface ISupportBundleWriter
{
    /// <summary>
    /// Writes a new support bundle and returns its path.
    /// </summary>
    Task<AbsolutePath> WriteAsync(CancellationToken cancellationToken = default);
}
