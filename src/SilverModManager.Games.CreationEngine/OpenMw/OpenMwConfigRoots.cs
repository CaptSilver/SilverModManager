using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.OpenMw;

/// <summary>
/// The native install's <c>$XDG_CONFIG_HOME/openmw</c> and the Flatpak's config folder, native first.
/// </summary>
public class OpenMwConfigRoots : IOpenMwConfigRoots
{
    private const string FlatpakAppId = "org.openmw.OpenMW";

    public OpenMwConfigRoots(IFileSystem fileSystem)
    {
        var home = fileSystem.GetKnownPath(KnownPath.HomeDirectory);
        var flatpakData = home / ".var/app" / FlatpakAppId;

        Roots =
        [
            new OpenMwConfigRoot(
                fileSystem.GetKnownPath(KnownPath.XDG_CONFIG_HOME) / "openmw",
                fileSystem.GetKnownPath(KnownPath.XDG_DATA_HOME) / "openmw",
                GameStore.OpenMW,
                // Debian puts games under /usr/games.
                () => FirstExisting(fileSystem, "/usr/bin/openmw", "/usr/games/openmw")),
            new OpenMwConfigRoot(
                flatpakData / "config/openmw",
                flatpakData / "data/openmw",
                GameStore.OpenMWFlatpak,
                () => IsFlatpakInstalled(fileSystem, home) ? fileSystem.FromUnsanitizedFullPath("/usr/bin/flatpak") : null),
        ];
    }

    public IReadOnlyList<OpenMwConfigRoot> Roots { get; }

    private static AbsolutePath? FirstExisting(IFileSystem fileSystem, params string[] candidates) =>
        candidates.Select(fileSystem.FromUnsanitizedFullPath).Cast<AbsolutePath?>().FirstOrDefault(path => path is { } candidate && candidate.FileExists);

    // The app is installed either system-wide or for the user; either way it is started through flatpak.
    private static bool IsFlatpakInstalled(IFileSystem fileSystem, AbsolutePath home) =>
        fileSystem.FromUnsanitizedFullPath("/var/lib/flatpak/app/" + FlatpakAppId).DirectoryExists()
        || (home / ".local/share/flatpak/app" / FlatpakAppId).DirectoryExists();
}
