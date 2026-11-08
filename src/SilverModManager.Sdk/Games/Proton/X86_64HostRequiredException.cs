using JetBrains.Annotations;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Sdk.Games;

/// <summary>
/// A Windows program was asked for on a machine that is not x86-64 and has no x86-64 interpreter that is
/// known to work, and the user has not said to try anyway.
/// </summary>
[PublicAPI]
public sealed class X86_64HostRequiredException(string message) : UserFacingException(message)
{
    /// <inheritdoc/>
    public override string Title => "x86-64 programs may not run here";
}
