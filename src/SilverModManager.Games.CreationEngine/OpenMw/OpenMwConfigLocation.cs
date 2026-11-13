using NexusMods.Paths;

namespace SilverModManager.Games.CreationEngine.OpenMw;

/// <summary>
/// Which config root holds an openmw.cfg, for the Morrowind row's inert <see cref="OpenMwConstants.OpenMwConfig"/>
/// location. With a config in both roots the native one is named; the OpenMW rows themselves each know their own root.
/// </summary>
public abstract record OpenMwConfigLocation
{
    public sealed record None : OpenMwConfigLocation;

    public sealed record One(AbsolutePath Folder) : OpenMwConfigLocation;

    public const string FileName = OpenMwConstants.ConfigFileName;

    /// <summary>A root counts only when it holds openmw.cfg: an empty config folder says nothing about use.</summary>
    public static OpenMwConfigLocation Locate(IOpenMwConfigRoots roots, IFileSystem fileSystem)
    {
        var found = roots.Candidates
            .Select(root => fileSystem.FromUnsanitizedFullPath(root.GetFullPath()))
            .Where(root => (root / FileName).FileExists)
            .ToList();

        return found.Count == 0 ? new None() : new One(found[0]);
    }
}
