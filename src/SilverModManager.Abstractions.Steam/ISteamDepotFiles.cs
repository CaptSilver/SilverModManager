using NexusMods.Paths;
using SilverModManager.Abstractions.Steam.Values;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Abstractions.Steam;

/// <summary>
/// Takes files out of Steam depot manifests through the user's Steam login: one file's bytes into the library, or a
/// manifest's file hashes without any bytes.
/// </summary>
public interface ISteamDepotFiles
{
    /// <summary>
    /// Fetches one file of a manifest and adds it to the library together with the manifest it came from. The bytes are
    /// checked against the SHA-1 the manifest lists, and against <paramref name="expected"/> when given; a file that does
    /// not match is never added.
    /// </summary>
    /// <param name="expected">The SHA-1 the caller was promised, or null to trust the manifest's own.</param>
    /// <param name="handler">Shows the QR code if the login has to be made now; null uses the registered handler.</param>
    Task<LibraryFile.ReadOnly> FetchFile(
        AppId app,
        DepotId depot,
        ManifestId manifestId,
        RelativePath path,
        Sha1Value? expected,
        IAcquisitionContext acquisition,
        CancellationToken cancellationToken,
        IAuthInterventionHandler? handler = null);

    /// <summary>
    /// The SHA-1 of every file in a manifest, so a game file the user already has can be recognised as that build's own.
    /// Steam's own copy of the manifest answers when it is cached, without a login; otherwise the manifest is fetched
    /// through the login.
    /// </summary>
    Task<ManifestFileHashes> VouchManifest(
        AppId app,
        DepotId depot,
        ManifestId manifestId,
        CancellationToken cancellationToken,
        IAuthInterventionHandler? handler = null);
}
