using SilverModManager.Games.Unity.BepInEx;
using SilverModManager.Games.Unity.MelonLoader;

namespace SilverModManager.Games.Unity;

public static class UnityModLoaders
{
    /// <summary>The loader a row names. The one place a <see cref="UnityModLoaderKind"/> becomes a class.</summary>
    public static AUnityModLoader Create(UnityGameDefinition definition) => definition.Loader switch
    {
        UnityModLoaderKind.BepInEx => new BepInExLoader(definition),
        UnityModLoaderKind.MelonLoader => new MelonLoaderLoader(definition),
        _ => throw new ArgumentOutOfRangeException(nameof(definition), definition.Loader, $"{definition.GameId} names a loader this build does not have."),
    };
}
