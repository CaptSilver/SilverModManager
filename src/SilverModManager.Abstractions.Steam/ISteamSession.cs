using SilverModManager.Abstractions.Steam.DTOs;
using SilverModManager.Abstractions.Steam.Values;
using NexusMods.Paths;

namespace SilverModManager.Abstractions.Steam;

/// <summary>
/// An abstraction for a Steam session.
/// </summary>
public interface ISteamSession
{
    /// <summary>
    /// Connect to the Steam session, performing any necessary setup.
    /// </summary>
    public Task Connect(CancellationToken token);

    /// <summary>
    /// Connect like <see cref="Connect(CancellationToken)"/>, showing the QR code of a login this call starts through
    /// <paramref name="handler"/> instead of the registered one. A verb passes its own terminal; the window's overlay
    /// would otherwise show a code the person at the terminal never sees.
    /// </summary>
    public Task Connect(IAuthInterventionHandler handler, CancellationToken token);

    /// <summary>
    /// Ends the current logon, so the next <see cref="Connect(CancellationToken)"/> logs in again from whatever login is
    /// stored then. Called when the stored login is forgotten: the session would otherwise stay logged on with it.
    /// </summary>
    public void LogOff();
    
    /// <summary>
    /// Get the product info for the specified app ID
    /// </summary>
    public Task<ProductInfo> GetProductInfoAsync(AppId appId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the manifest data for a specific manifest
    /// </summary>
    public Task<Manifest> GetManifestContents(AppId appId, DepotId depotId, ManifestId manifestId, string branch, CancellationToken token = default);

    /// <summary>
    /// Get a readable, seekable, stream for the specified file in the specified manifest
    /// </summary>
    public Stream GetFileStream(AppId appId, Manifest manifest, RelativePath file);
}
