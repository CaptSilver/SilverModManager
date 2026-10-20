using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;

namespace NexusMods.Sdk.Games;

/// <summary>
/// The outcome of picking a Proton build for a game: either a build, or the reason there is none.
/// </summary>
[PublicAPI]
public sealed record ProtonResolution
{
    /// <summary>
    /// The build to use, set when <see cref="Diagnostic"/> is not.
    /// </summary>
    public ProtonBuild? Build { get; private init; }

    /// <summary>
    /// Why no build could be chosen, set when <see cref="Build"/> is not.
    /// </summary>
    public string? Diagnostic { get; private init; }

    /// <summary>
    /// Whether a build was chosen.
    /// </summary>
    [MemberNotNullWhen(true, nameof(Build))]
    [MemberNotNullWhen(false, nameof(Diagnostic))]
    public bool IsResolved => Build is not null;

    /// <summary/>
    public static ProtonResolution Resolved(ProtonBuild build) => new() { Build = build };

    /// <summary/>
    public static ProtonResolution Failed(string diagnostic) => new() { Diagnostic = diagnostic };
}
