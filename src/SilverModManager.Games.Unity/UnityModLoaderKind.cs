namespace SilverModManager.Games.Unity;

/// <summary>The mod loader a <see cref="UnityGameDefinition"/> row uses; <see cref="UnityModLoaders.Create"/> turns it into the loader.</summary>
public enum UnityModLoaderKind
{
    BepInEx,
    MelonLoader,
}
