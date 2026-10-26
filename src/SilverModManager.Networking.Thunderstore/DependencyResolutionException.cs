using JetBrains.Annotations;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Networking.Thunderstore;

/// <summary>
/// A package's dependencies could not be resolved, for example because one names a package that does not exist.
/// </summary>
[PublicAPI]
public sealed class DependencyResolutionException(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "The dependencies could not be resolved";
}
