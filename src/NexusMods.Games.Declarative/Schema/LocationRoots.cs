namespace NexusMods.Games.Declarative.Schema;

/// <summary>
/// The tokens a location's <c>root</c> may use. They resolve through the file system the game's
/// locator hands to <c>GetLocations</c>, so under Proton they land in the prefix; a definition never
/// names a host path.
/// </summary>
public static class LocationRoots
{
    /// <summary>The folder the game was found in.</summary>
    public const string Install = "install";
    public const string LocalAppData = "%LOCALAPPDATA%";
    public const string AppData = "%APPDATA%";
    public const string MyGames = "%MYGAMES%";
    public const string Documents = "%DOCUMENTS%";
    public const string SavedGames = "%SAVEDGAMES%";

    public static readonly IReadOnlyList<string> All = [Install, LocalAppData, AppData, MyGames, Documents, SavedGames];

    public static bool IsKnown(string root) => All.Contains(root, StringComparer.Ordinal);
}
