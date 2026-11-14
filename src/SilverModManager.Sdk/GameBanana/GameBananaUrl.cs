using System.Globalization;
using JetBrains.Annotations;

namespace SilverModManager.Sdk.GameBanana;

/// <summary>
/// <c>silvermodmanager://mod/install/{modId}/{fileId}</c>, the link GameBanana's "One-Click Install" button builds for
/// a mod manager it knows (<c>{alias}://mod/install/{modId}/{fileId}</c>). It names one file of one mod and no game.
/// </summary>
[PublicAPI]
public readonly record struct GameBananaUrl(long ModId, long FileId)
{
    /// <summary>The URI scheme the app claims for these links.</summary>
    public const string Scheme = "silvermodmanager";

    private const string Expected = "silvermodmanager://mod/install/{modId}/{fileId}";

    /// <summary>
    /// Parses an install link. On failure <paramref name="problem"/> says what is wrong and quotes the link.
    /// </summary>
    public static bool TryParse(Uri uri, out GameBananaUrl url, out string problem)
    {
        url = default;

        // Segments after the leading "/": install, mod id, file id.
        var segments = uri.IsAbsoluteUri ? uri.Segments.Skip(1).Select(static s => Uri.UnescapeDataString(s.TrimEnd('/'))).ToArray() : [];
        if (uri.IsAbsoluteUri
            && uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase)
            && uri.Host.Equals("mod", StringComparison.OrdinalIgnoreCase)
            && segments is ["install", var mod, var file]
            && long.TryParse(mod, NumberStyles.None, CultureInfo.InvariantCulture, out var modId) && modId > 0
            && long.TryParse(file, NumberStyles.None, CultureInfo.InvariantCulture, out var fileId) && fileId > 0)
        {
            url = new GameBananaUrl(modId, fileId);
            problem = string.Empty;
            return true;
        }

        problem = $"Cannot open '{uri.OriginalString}': only {Expected} links with a numeric mod and file id are understood.";
        return false;
    }
}
