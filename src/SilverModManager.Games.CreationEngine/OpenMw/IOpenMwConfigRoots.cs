using NexusMods.Paths;

namespace SilverModManager.Games.CreationEngine.OpenMw;

/// <summary>
/// The places an OpenMW install can live. Kept behind a service so that the one place that knows the real
/// locations can be swapped for temp folders in tests: the test host's file system is the real one, and looking
/// these up directly would find and rewrite the developer's own config.
/// </summary>
public interface IOpenMwConfigRoots
{
    /// <summary>The native root first, then the Flatpak's.</summary>
    IReadOnlyList<OpenMwConfigRoot> Roots { get; }

    IReadOnlyList<AbsolutePath> Candidates => Roots.Select(root => root.ConfigFolder).ToList();
}
