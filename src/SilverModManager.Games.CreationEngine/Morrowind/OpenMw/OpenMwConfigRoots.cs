using NexusMods.Paths;

namespace SilverModManager.Games.CreationEngine.Morrowind.OpenMw;

/// <summary>
/// The native install's <c>$XDG_CONFIG_HOME/openmw</c> and the Flatpak's config folder, in that order: the
/// diagnostic for two configs names them native first.
/// </summary>
public class OpenMwConfigRoots(IFileSystem fileSystem) : IOpenMwConfigRoots
{
    public IReadOnlyList<AbsolutePath> Candidates { get; } =
    [
        fileSystem.GetKnownPath(KnownPath.XDG_CONFIG_HOME) / "openmw",
        fileSystem.GetKnownPath(KnownPath.HomeDirectory) / ".var/app/org.openmw.OpenMW/config/openmw",
    ];
}
