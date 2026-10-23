namespace SilverModManager.Games.FromSoft.Me3;

/// <summary>
/// A native DLL that arrives as a file of the overlay rather than as a native mod, and that the profile loads from
/// the game folder, where the overlay puts it for the length of a session.
/// </summary>
/// <param name="TriggerPathInMods">The overlay path inside the mods folder whose presence adds the native to the profile.</param>
/// <param name="GameRelativeDllPath">The DLL's path inside the game folder, as the overlay lays it out.</param>
/// <param name="OwnsSave">True when the DLL keeps its own save, so a profile that loads it writes no savefile line.</param>
public sealed record Me3OverlayNative(string TriggerPathInMods, string GameRelativeDllPath, bool OwnsSave);
