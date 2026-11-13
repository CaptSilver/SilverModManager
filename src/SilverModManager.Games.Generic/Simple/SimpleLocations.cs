using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Generic.Simple;

/// <summary>
/// One location id per known folder, shared by every simple game. A location is per installation, so two games can
/// use the same id for different folders. A id per game would be a 16-bit hash of a string that is persisted in
/// every <see cref="GamePath"/>, with a collision decided by which other ids exist, so it could not be fixed by a rename.
/// </summary>
public static class SimpleLocations
{
    public static readonly LocationId Documents = LocationId.From("Simple.Documents", "Documents", "the Documents folder");
    public static readonly LocationId LocalAppData = LocationId.From("Simple.LocalAppData", "Local AppData", "the Local AppData folder");
    public static readonly LocationId RoamingAppData = LocationId.From("Simple.RoamingAppData", "Roaming AppData", "the Roaming AppData folder");

    public static readonly LocationId LocalAppDataLow = LocationId.From("Simple.LocalAppDataLow", "LocalLow AppData", "the LocalLow AppData folder");

    /// <summary>The location a mod root of <paramref name="root"/> is mapped to.</summary>
    public static LocationId For(ModLocationRoot root) => root switch
    {
        ModLocationRoot.GameFolder => LocationId.Game,
        ModLocationRoot.Documents => Documents,
        ModLocationRoot.LocalAppData => LocalAppData,
        ModLocationRoot.RoamingAppData => RoamingAppData,
        ModLocationRoot.LocalAppDataLow => LocalAppDataLow,
        _ => throw new ArgumentOutOfRangeException(nameof(root), root, "Unknown mod location root."),
    };

    /// <summary>The Windows known folder a non-game <paramref name="root"/> starts at, resolved in <paramref name="fileSystem"/>.</summary>
    public static AbsolutePath Resolve(IFileSystem fileSystem, ModLocationRoot root) => root switch
    {
        ModLocationRoot.Documents => fileSystem.GetKnownPath(KnownPath.MyDocumentsDirectory),
        ModLocationRoot.LocalAppData => fileSystem.GetKnownPath(KnownPath.LocalApplicationDataDirectory),
        ModLocationRoot.RoamingAppData => fileSystem.GetKnownPath(KnownPath.ApplicationDataDirectory),
        // Windows keeps LocalLow beside Local, and the known-path enum has no entry for it.
        ModLocationRoot.LocalAppDataLow => fileSystem.GetKnownPath(KnownPath.LocalApplicationDataDirectory).Parent.Combine("LocalLow"),
        _ => throw new ArgumentOutOfRangeException(nameof(root), root, "The game folder is not a known folder."),
    };
}
