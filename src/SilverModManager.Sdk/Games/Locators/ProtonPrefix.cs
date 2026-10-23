using JetBrains.Annotations;
using NexusMods.Paths;

namespace SilverModManager.Sdk.Games;

/// <summary>
/// Answers whether a Proton prefix has been created.
/// </summary>
[PublicAPI]
public static class ProtonPrefix
{
    /// <summary>
    /// Whether Proton has created the prefix at <paramref name="prefixDirectory"/> (the <c>pfx</c> directory).
    /// </summary>
    /// <remarks>
    /// Checks for <c>system.reg</c> rather than for the directory: Steam creates <c>compatdata/&lt;appid&gt;</c>
    /// before Proton has written anything into it, and an empty or half-written prefix has no registry yet.
    /// </remarks>
    public static bool IsReady(AbsolutePath prefixDirectory) => prefixDirectory.Combine("system.reg").FileExists;
}
