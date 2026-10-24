using System.Diagnostics.CodeAnalysis;

namespace SilverModManager.Sdk.Games.Versions;

internal static class VersionText
{
    /// <summary>
    /// Parses <c>1.6.15</c>, <c>1.6.15-alpha.1</c> and <c>1.6.15+abc123</c>; the prerelease and build suffix is dropped.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> text, [NotNullWhen(true)] out Version? version)
    {
        var end = text.IndexOfAny('-', '+');
        if (end >= 0) text = text[..end];
        return Version.TryParse(text, out version);
    }
}
