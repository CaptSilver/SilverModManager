using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;
using NexusMods.Paths;

namespace NexusMods.Sdk.Games;

/// <summary>
/// The Wine prefix a game runs in.
/// </summary>
/// <param name="Path">The prefix directory (the <c>pfx</c> directory for Proton).</param>
/// <param name="Kind">Which launcher or runner owns it.</param>
/// <param name="WineBinary">The <c>wine</c> executable to run programs with, for prefixes that are not Proton's.</param>
/// <param name="ProtonAppId">The Steam app id whose Proton prefix this is, for <see cref="CompatibilityLayerKind.SteamProton"/>.</param>
[PublicAPI]
public sealed record ResolvedPrefix(AbsolutePath Path, CompatibilityLayerKind Kind, AbsolutePath? WineBinary = null, uint? ProtonAppId = null);

/// <summary>
/// The outcome of finding a game's prefix: a prefix, the statement that the game needs none, or the reason
/// none could be chosen.
/// </summary>
[PublicAPI]
public sealed record PrefixResolution
{
    /// <summary>
    /// The prefix, set when <see cref="IsResolved"/>.
    /// </summary>
    public ResolvedPrefix? Prefix { get; private init; }

    /// <summary>
    /// Why there is no prefix, set when the game needs one but none could be chosen, and also when it needs
    /// none (a native Linux build).
    /// </summary>
    public string? Diagnostic { get; private init; }

    /// <summary>
    /// Whether the game is a native Linux build that runs without a prefix.
    /// </summary>
    public bool IsNative { get; private init; }

    /// <summary>
    /// Whether a prefix was chosen.
    /// </summary>
    [MemberNotNullWhen(true, nameof(Prefix))]
    public bool IsResolved => Prefix is not null;

    /// <summary>
    /// Whether the game needs a prefix and none could be chosen. Mutually exclusive with
    /// <see cref="IsResolved"/> and <see cref="IsNative"/>.
    /// </summary>
    [MemberNotNullWhen(true, nameof(Diagnostic))]
    public bool IsFailed => !IsResolved && !IsNative;

    /// <summary/>
    public static PrefixResolution Resolved(ResolvedPrefix prefix) => new() { Prefix = prefix };

    /// <summary/>
    public static PrefixResolution Native(string explanation) => new() { IsNative = true, Diagnostic = explanation };

    /// <summary/>
    public static PrefixResolution Failed(string diagnostic) => new() { Diagnostic = diagnostic };
}
