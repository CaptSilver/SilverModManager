using JetBrains.Annotations;
using SilverModManager.Networking.GitHub.DTOs;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Networking.GitHub;

/// <summary>
/// Helpers for choosing and verifying release assets.
/// </summary>
[PublicAPI]
public static class ReleaseAssets
{
    /// <summary>
    /// Picks the first asset ending in <paramref name="suffix"/> whose name contains <paramref name="nameHint"/>
    /// (case-insensitive), else the first asset ending in the suffix, else null. Releases that ship more than one
    /// asset of a kind (a debug and a release zip) need the hint; a stale hint falls back instead of failing.
    /// </summary>
    public static Asset? Pick(Release release, string suffix, string? nameHint)
    {
        var candidates = release.Assets.Where(asset => asset.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (!string.IsNullOrEmpty(nameHint))
        {
            var hinted = candidates.FirstOrDefault(asset => asset.Name.Contains(nameHint, StringComparison.OrdinalIgnoreCase));
            if (hinted is not null) return hinted;
        }

        return candidates.FirstOrDefault();
    }

    /// <summary>
    /// The SHA-256 GitHub reports for the asset, or null when it reports none or one of another algorithm.
    /// </summary>
    /// <exception cref="FormatException">The digest claims to be SHA-256 but is not 64 hex characters.</exception>
    public static Sha256Value? GetSha256(this Asset asset)
    {
        const string prefix = "sha256:";
        if (asset.Digest is null || !asset.Digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        var hex = asset.Digest.AsSpan(prefix.Length);
        if (!Sha256Value.TryParse(hex, out var value))
            throw new FormatException($"Asset '{asset.Name}' has a malformed SHA-256 digest '{asset.Digest}'.");
        return value;
    }
}
