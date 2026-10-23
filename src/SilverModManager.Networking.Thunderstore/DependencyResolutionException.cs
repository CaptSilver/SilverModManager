using JetBrains.Annotations;

namespace SilverModManager.Networking.Thunderstore;

/// <summary>
/// A package's dependencies could not be resolved, for example because one names a package that does not exist.
/// </summary>
[PublicAPI]
public sealed class DependencyResolutionException(string message) : Exception(message);
