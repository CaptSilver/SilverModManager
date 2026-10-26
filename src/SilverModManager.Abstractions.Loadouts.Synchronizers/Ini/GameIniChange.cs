namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;

/// <summary>What an apply does with a setting the game changed in an INI the loadout owns.</summary>
public enum GameIniChangeKind
{
    /// <summary>Nothing else sets it, so it becomes part of the loadout's copy of the file.</summary>
    Adopted,

    /// <summary>The app owns the key and writes its own value back.</summary>
    ManagedKeyReset,

    /// <summary>An enabled mod's tweak sets the key, and the tweak wins.</summary>
    TweakOverrides,
}

/// <summary>A setting that differs between the INI the game left and what the app last wrote.</summary>
/// <param name="Value">The value the game left in the file.</param>
/// <param name="Owner">The mod (or the loadout's own tweak) that sets the key, for <see cref="GameIniChangeKind.TweakOverrides"/>.</param>
public readonly record struct GameIniChange(GameIniChangeKind Kind, string Section, string Key, string Value, string? Owner = null);
