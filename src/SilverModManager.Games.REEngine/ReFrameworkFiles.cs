namespace SilverModManager.Games.REEngine;

/// <summary>Names of the files and folders REFramework and the RE Engine read from the game folder.</summary>
internal static class ReFrameworkFiles
{
    /// <summary>The loader: REFramework is a dinput8.dll proxy placed next to the game executable.</summary>
    public const string Loader = "dinput8.dll";

    /// <summary>REFramework's folder for Lua scripts (autorun) and plugin DLLs (plugins).</summary>
    public const string Root = "reframework";

    /// <summary>Loose game files the RE Engine reads in place of the ones in its archives; needs no loader.</summary>
    public const string Natives = "natives";

    /// <summary>The log REFramework writes at runtime, and the previous run's copy of it.</summary>
    public const string Log = "re2_framework_log.txt";
    public const string PreviousLog = "re2_framework_log.prev.txt";
}
