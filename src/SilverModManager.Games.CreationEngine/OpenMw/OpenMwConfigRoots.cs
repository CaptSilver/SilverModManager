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
                () => FirstExisting(fileSystem, "/usr/bin/openmw", "/usr/games/openmw"),
                // The engine reads a config next to the binary first and the system one only when there is none
                // (configurationmanager.cpp), so the binary's folder wins.
                () => NativeGlobalConfig(fileSystem)),
            new OpenMwConfigRoot(
                flatpakData / "config/openmw",
                flatpakData / "data/openmw",
                GameStore.OpenMWFlatpak,
                // No global config: the app's shipped openmw.cfg is expected under
                // <app>/current/active/files/etc/openmw (the manifest builds with prefix /app), but that layout has not
                // been checked against an installed Flatpak, and reading the wrong file would invent early loaders.
                () => IsFlatpakInstalled(fileSystem, home) ? fileSystem.FromUnsanitizedFullPath("/usr/bin/flatpak") : null),
        ];
    }

    public IReadOnlyList<OpenMwConfigRoot> Roots { get; }

    private static AbsolutePath? NativeGlobalConfig(IFileSystem fileSystem)
    {
        var program = FirstExisting(fileSystem, "/usr/bin/openmw", "/usr/games/openmw");
        if (program?.Parent / "openmw.cfg" is { } local && local.FileExists) return local;
        return fileSystem.FromUnsanitizedFullPath("/etc/openmw/openmw.cfg");
    }

    private static AbsolutePath? FirstExisting(IFileSystem fileSystem, params string[] candidates) =>
        candidates.Select(fileSystem.FromUnsanitizedFullPath).Cast<AbsolutePath?>().FirstOrDefault(path => path is { } candidate && candidate.FileExists);

    // The app is installed either system-wide or for the user; either way it is started through flatpak.
    private static bool IsFlatpakInstalled(IFileSystem fileSystem, AbsolutePath home) =>
        fileSystem.FromUnsanitizedFullPath("/var/lib/flatpak/app/" + FlatpakAppId).DirectoryExists()
        || (home / ".local/share/flatpak/app" / FlatpakAppId).DirectoryExists();
}
