using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Games.FromSoft.Heal;

/// <summary>The SHA-1 of a file in the game folder, the hash Steam's manifests list.</summary>
internal static class FileSha1
{
    /// <summary>The SHA-1 of the file, or null when it is missing or cannot be read; an unreadable file is logged and not called changed.</summary>
    public static async Task<Sha1Value?> Of(AbsolutePath path, CancellationToken cancellationToken, ILogger? logger = null)
    {
        try
        {
            if (!path.FileExists) return null;
            return await FileHashing.Digest<Sha1Value, SHA1, Sha1Hasher>(path, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(exception, "{Path} could not be read to compare it with Steam's manifest", path);
            return null;
        }
    }
}
