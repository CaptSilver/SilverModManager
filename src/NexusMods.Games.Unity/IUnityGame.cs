using NexusMods.Abstractions.Games;
using NexusMods.Games.Unity.BepInEx;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.Unity;

/// <summary>A Unity game whose mods are BepInEx plugins from Thunderstore.</summary>
public interface IUnityGame : IGame, IWineDllOverrideRequirements, IThunderstoreGame
{
    UnityGameDefinition Definition { get; }

    BepInExLoader Loader { get; }
}
