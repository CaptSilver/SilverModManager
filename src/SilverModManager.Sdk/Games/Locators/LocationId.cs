using SilverModManager.Sdk.Hashes;
using System.Collections.Concurrent;
using TransparentValueObjects;

namespace SilverModManager.Sdk.Games;

/// <summary>
/// The base folder for the GamePath.
/// </summary>
[ValueObject<ushort>]
public readonly partial struct LocationId
{
    private static readonly StringHashPool<ushort, FNV1a16Hasher> HashPool = new(name: nameof(LocationId));
    private static readonly ConcurrentDictionary<ushort, string> DisplayNames = new();

    /// <summary>What a player is told for a location nobody gave a name.</summary>
    public const string UnnamedDisplayName = "Other folders";

    /// <summary>
    /// Converts the string to a LocationId.
    /// </summary>
    public static LocationId From(string value) => From(HashPool.GetOrAdd(value));

    /// <inheritdoc />
    public override string ToString() => HashPool[Value];

    /// <summary>
    /// The location as a player names it ("Settings", not "Preferences"): the name that heads a folder row
    /// ("Settings/Foo") and ends "in Settings". A location nobody named reads <see cref="UnnamedDisplayName"/>,
    /// never its internal id. Only the app's own locations are named here; a game names its own through
    /// <see cref="IGameData.LocationNames"/>, which does not depend on static initialization order.
    /// </summary>
    public string DisplayName => DisplayNames.TryGetValue(Value, out var name) ? name : UnnamedDisplayName;

    /// <summary>Whether a name was registered for this location. Game-specific names come from the game instead.</summary>
    public bool IsNamed => DisplayNames.ContainsKey(Value);

    /// <summary>Converts the string to a LocationId and registers the name a player knows it by.</summary>
    public static LocationId From(string value, string displayName)
    {
        var id = From(value);
        DisplayNames[id.Value] = displayName;
        return id;
    }

    /// <summary>
    /// Unknown game folder type, used for default values. Unnamed, so it reads as <see cref="UnnamedDisplayName"/>.
    /// </summary>
    public static readonly LocationId Unknown = From("Unknown");

    /// <summary>
    /// The path for the game installation.
    /// </summary>
    public static readonly LocationId Game = From("Game", "Game folder");

    /// <summary>
    /// Path used to store the save data of a game.
    /// </summary>
    public static readonly LocationId Saves = From("Saves", "Saves");

    /// <summary>
    /// Path used to store player settings/preferences.
    /// </summary>
    public static readonly LocationId Preferences = From("Preferences", "Settings");

    /// <summary>
    /// Path for game files located under LocalAppdata or equivalent.
    /// </summary>
    public static readonly LocationId AppData = From("AppData", "AppData");

    /// <summary>
    /// Path for game files located under Appdata/Roaming or equivalent.
    /// </summary>
    public static readonly LocationId AppDataRoaming = From("AppDataRoaming", "AppData");

    /// <summary>
    /// Where Elden Ring's me3 packages and native mods are deployed, outside the game folder.
    /// </summary>
    public static readonly LocationId Me3Mods = From("Me3Mods", "Mods");

    /// <summary>
    /// Where Elden Ring's me3 mod loader is deployed, outside the game folder.
    /// </summary>
    public static readonly LocationId Me3Host = From("Me3Host", "Mod loader folder");
}
