using JetBrains.Annotations;

namespace NexusMods.Sdk.Games;

/// <summary>
/// How a tool is run. <see cref="Auto"/> lets <see cref="IToolRunner"/> pick from the game's prefix; any other
/// value forces that way and nothing else is tried if it cannot run.
/// </summary>
/// <remarks>
/// The values are persisted as numbers, so existing ones must keep theirs.
/// </remarks>
[PublicAPI]
public enum ToolRunVia : byte
{
    /// <summary>Whatever suits the game's prefix.</summary>
    Auto = 0,

    /// <summary>The Proton build Steam uses for the game.</summary>
    SteamProton = 1,

    /// <summary>umu-run, or the runner's proton script when umu is not installed.</summary>
    Umu = 2,

    /// <summary>A wine binary with the game's prefix.</summary>
    Wine = 3,

    /// <summary>The wine runner inside Heroic's Flatpak sandbox.</summary>
    HeroicFlatpak = 4,

    /// <summary>The program is a Linux executable and is started directly, outside any prefix.</summary>
    Native = 5,
}
