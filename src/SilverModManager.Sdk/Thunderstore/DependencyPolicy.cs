using JetBrains.Annotations;

namespace SilverModManager.Sdk.Thunderstore;

/// <summary>
/// Which version of a dependency to install when a package lists one.
/// </summary>
[PublicAPI]
public enum DependencyPolicy
{
    /// <summary>
    /// Install the latest active version of every dependency, direct and transitive, whatever version the
    /// manifests name. This is what other Thunderstore managers do for ordinary mods: the version in a manifest
    /// is the one the author built against, and authors rarely republish when a dependency gets a fix.
    /// </summary>
    Latest,

    /// <summary>
    /// Install exactly the versions the root package lists, and let them win over any transitive request for
    /// the same package; transitive dependencies the root does not list still take the latest. Used for
    /// modpacks, whose listed versions are the set that was tested together.
    /// </summary>
    Exact,
}
