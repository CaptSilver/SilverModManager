using SilverModManager.Abstractions.Games;
using SilverModManager.Games.Unity.BepInEx;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Unity;

/// <summary>A Unity game whose mods are BepInEx plugins from Thunderstore.</summary>
public interface IUnityGame : IGame, IWineDllOverrideRequirements, IThunderstoreGame
{
    UnityGameDefinition Definition { get; }

    BepInExLoader Loader { get; }
}
