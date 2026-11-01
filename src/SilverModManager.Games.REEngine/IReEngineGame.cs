using SilverModManager.Abstractions.Games;

namespace SilverModManager.Games.REEngine;

/// <summary>An RE Engine game whose mods are loose files and REFramework scripts and plugins.</summary>
public interface IReEngineGame : IGame, IWineDllOverrideRequirements
{
    ReEngineGameDefinition Definition { get; }

    ReFrameworkLoader Loader { get; }
}
