using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Games;

/// <summary>
/// A game whose mods can hook the game's own code, so a new build of the game can break them. The game names
/// the paths where such files land; <see cref="NativeCodeMods"/> uses that to say which installed mods are at risk.
/// </summary>
public interface IGameNativeCode
{
    /// <summary>
    /// True when a file installed at <paramref name="path"/> loads into the game's process or is a script
    /// extender runtime: a library that was compiled against one build of the game. Data files such as plugins,
    /// archives and textures are not.
    /// </summary>
    bool HooksGameCode(GamePath path);

    /// <summary>
    /// What a native file at <paramref name="path"/> is to the loader that brings it in, or null for a file that
    /// is not loader code. Every path <see cref="HooksGameCode"/> accepts is at least a plugin; games also name
    /// the loader's own runtime files, which <see cref="HooksGameCode"/> leaves out so baseline classification
    /// does not change.
    /// </summary>
    NativeCodeRole? RoleOf(GamePath path) => HooksGameCode(path) ? NativeCodeRole.LoaderPlugin : null;
}

/// <summary>Which part of a mod loader a native file is.</summary>
public enum NativeCodeRole : byte
{
    /// <summary>A library the loader loads from its plugin folder.</summary>
    LoaderPlugin,

    /// <summary>The loader itself: its launcher, proxy DLL or core libraries.</summary>
    LoaderRuntime,
}

/// <summary>Helpers for the path rules games share when they implement <see cref="IGameNativeCode"/>.</summary>
public static class NativeCodePaths
{
    private static readonly Extension Dll = new(".dll");

    /// <summary>True for a <c>.dll</c> anywhere below <paramref name="folder"/>.</summary>
    public static bool IsLibraryBelow(GamePath path, GamePath folder)
        => path.Extension == Dll && path.InFolder(folder);

    /// <summary>True for a file of one of <paramref name="extensions"/> directly in <paramref name="folder"/>, not in a subfolder.</summary>
    public static bool IsDirectlyIn(GamePath path, GamePath folder, params Extension[] extensions)
        => path.LocationId == folder.LocationId
           && path.Path.Parent == folder.Path
           && extensions.Contains(path.Extension);
}
