using JetBrains.Annotations;
using NexusMods.Paths;

namespace NexusMods.Sdk.Games;

/// <summary>
/// What a locator found a Windows game running on, as opposed to the store it was bought from: a GOG game
/// found through Heroic and a GOG game found inside a plain Wine prefix are both <see cref="GameStore.GOG"/>.
/// </summary>
/// <param name="Kind">Which launcher or runner owns the prefix.</param>
/// <param name="Prefix">The Wine prefix directory (the <c>pfx</c> directory for Proton).</param>
/// <param name="WineBinary">The <c>wine</c> executable the launcher picked for the game, when the locator knows it.</param>
/// <param name="RunnerName">The launcher's name for the runner, such as <c>Wine-GE-Proton8-26</c>, when the locator knows it.</param>
[PublicAPI]
public sealed record CompatibilityLayer(CompatibilityLayerKind Kind, AbsolutePath Prefix, AbsolutePath? WineBinary = null, string? RunnerName = null);

/// <summary>
/// The launcher or runner that owns a game's Wine prefix.
/// </summary>
[PublicAPI]
public enum CompatibilityLayerKind
{
    /// <summary>A Steam library game run by Proton; the prefix is <c>compatdata/&lt;appid&gt;/pfx</c>.</summary>
    SteamProton,

    /// <summary>A game Heroic installed and runs with its own prefix and runner.</summary>
    Heroic,

    /// <summary>Same as <see cref="Heroic"/>, for the Flatpak build of Heroic.</summary>
    HeroicFlatpak,

    /// <summary>A game found inside a Wine prefix that no launcher manages.</summary>
    PlainWine,
}
