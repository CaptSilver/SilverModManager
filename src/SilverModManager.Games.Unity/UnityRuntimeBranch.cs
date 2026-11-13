using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Unity;

/// <summary>Which scripting backend a Unity build uses. A mod is compiled for one of them.</summary>
public enum UnityRuntime
{
    Unknown,
    Il2Cpp,
    Mono,
}

/// <summary>Tells an IL2CPP install from a Mono one, and a DLL built for either.</summary>
public static class UnityRuntimeBranch
{
    /// <summary>
    /// Probes the located game folder: <c>GameAssembly.dll</c> beside the executable is IL2CPP, a managed
    /// <c>Assembly-CSharp.dll</c> below the data folder is Mono. It reads the disk rather than the loadout so a
    /// diagnostic and an installer give the same answer, and the data folder comes from the definition because
    /// it is named after the executable.
    /// </summary>
    public static UnityRuntime Of(GameInstallation installation, UnityGameDefinition definition)
    {
        var gameFolder = installation.Locations[LocationId.Game].Path;
        var executableFolder = gameFolder.Combine(definition.Executable).Parent;

        if (executableFolder.Combine("GameAssembly.dll").FileExists) return UnityRuntime.Il2Cpp;
        if (executableFolder.Combine(definition.DataFolder).Combine("Managed/Assembly-CSharp.dll").FileExists) return UnityRuntime.Mono;
        return UnityRuntime.Unknown;
    }

    /// <summary>
    /// The branch a DLL says it is built for: a <c>Il2Cpp</c> or <c>Mono</c> token in its name, split on dots,
    /// underscores and dashes. A name that merely contains the letters, such as <c>Monolith.dll</c>, says nothing.
    /// </summary>
    public static UnityRuntime OfFileName(string dllName)
    {
        foreach (var token in Path.GetFileNameWithoutExtension(dllName).Split('.', '_', '-'))
        {
            if (token.Equals("Il2Cpp", StringComparison.OrdinalIgnoreCase)) return UnityRuntime.Il2Cpp;
            if (token.Equals("Mono", StringComparison.OrdinalIgnoreCase)) return UnityRuntime.Mono;
        }

        return UnityRuntime.Unknown;
    }
}
