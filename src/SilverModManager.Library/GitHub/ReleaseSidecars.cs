using Microsoft.Extensions.Logging;
using SilverModManager.Networking.GitHub.DTOs;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Library.GitHub;

/// <summary>
/// Fetches the checksum files of a release, so a download can be checked against them.
/// </summary>
internal static class ReleaseSidecars
{
    // A checksum file is a few lines; anything bigger is not one.
    private const int MaxBytes = 1 << 20;

    /// <summary>
    /// The digests every checksum asset of the release lists, or null when it has none or none could be read. The
    /// download is an extra check the release may not offer, so a file that cannot be fetched is logged and skipped,
    /// not a reason to refuse the install.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, Sha256Value>?> Read(HttpClient client, Release release, ILogger logger, CancellationToken cancellationToken)
    {
        Dictionary<string, Sha256Value>? merged = null;
        foreach (var asset in release.Assets.Where(asset => ReleaseSums.IsSidecarName(asset.Name)))
        {
            try
            {
                using var response = await client.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int read;
                while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
                {
                    if (buffer.Length + read > MaxBytes) throw new InvalidDataException($"The checksum file {asset.Name} is larger than {MaxBytes} bytes.");
                    buffer.Write(chunk, 0, read);
                }

                var sums = ReleaseSums.Parse(asset.Name, System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
                merged ??= new Dictionary<string, Sha256Value>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, digest) in sums.Digests) merged[name] = digest;
            }
            catch (Exception e) when (e is HttpRequestException or InvalidDataException or IOException)
            {
                logger.LogWarning(e, "Could not read the checksum file {Asset} of release {Tag}; the download is not checked against it", asset.Name, release.TagName);
            }
        }

        return merged;
    }
}
