using JetBrains.Annotations;

namespace SilverModManager.Sdk.Games;

/// <summary>
/// What a program started through <see cref="IToolRunner.RunWindowsTool"/> is, which decides whether the
/// running-game detector counts it as the game.
/// </summary>
[PublicAPI]
public enum ToolRunPurpose
{
    /// <summary>A modding tool or helper. The process is registered as a started tool, so it is never taken for the game.</summary>
    Tool,

    /// <summary>The game itself. Nothing is registered; the detector finds it like any other game process.</summary>
    Game,
}
