namespace SilverModManager.Games.LoadOrderFile.Manifests;

/// <summary>
/// What a game's mod manifest says about a mod, in the spelling the game's order file lists mods by. The order lists
/// hold other mods' ids.
/// </summary>
public sealed record ModManifest(
    string Id,
    string? Name,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> LoadAfter,
    IReadOnlyList<string> LoadBefore,
    IReadOnlyList<string> ForceLoadAfter,
    IReadOnlyList<string> ForceLoadBefore,
    IReadOnlyList<string> IncompatibleWith);
