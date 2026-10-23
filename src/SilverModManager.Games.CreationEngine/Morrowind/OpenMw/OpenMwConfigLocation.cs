using NexusMods.Paths;

namespace NexusMods.Games.CreationEngine.Morrowind.OpenMw;

/// <summary>Which config root holds an openmw.cfg.</summary>
public abstract record OpenMwConfigLocation
{
    public sealed record None : OpenMwConfigLocation;

    public sealed record One(AbsolutePath Folder) : OpenMwConfigLocation;

    /// <summary>Native and Flatpak OpenMW both have a config; which one the user runs is not something to guess.</summary>
    public sealed record Both(AbsolutePath First, AbsolutePath Second) : OpenMwConfigLocation;

    public const string FileName = "openmw.cfg";

    /// <summary>A root counts only when it holds openmw.cfg: an empty config folder says nothing about use.</summary>
    public static OpenMwConfigLocation Locate(IOpenMwConfigRoots roots, IFileSystem fileSystem)
    {
        var found = roots.Candidates
            .Select(root => fileSystem.FromUnsanitizedFullPath(root.GetFullPath()))
            .Where(root => (root / FileName).FileExists)
            .ToList();

        return found.Count switch
        {
            0 => new None(),
            1 => new One(found[0]),
            _ => new Both(found[0], found[1]),
        };
    }
}
