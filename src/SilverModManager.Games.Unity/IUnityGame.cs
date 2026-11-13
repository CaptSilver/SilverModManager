using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Unity;

/// <summary>A Unity game whose mods are loaded by a loader from Thunderstore.</summary>
public interface IUnityGame : IGame, IWineDllOverrideRequirements, IThunderstoreGame
{
    UnityGameDefinition Definition { get; }

    AUnityModLoader Loader { get; }
}
