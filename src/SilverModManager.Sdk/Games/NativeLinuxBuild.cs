using NexusMods.Paths;

namespace SilverModManager.Sdk.Games;

public static class NativeLinuxBuild
{
    /// <summary>
    /// Whether the install in <paramref name="installFolder"/> is the game's native Linux build: the game has one
    /// and its native executable is in the folder. The Windows build of the same game has no such file.
    /// </summary>
    public static bool IsInstalledAt(this IGameData game, AbsolutePath installFolder)
        => game is { HasNativeLinuxBuild: true, NativeLinuxPrimaryFile: { } file } && installFolder.Combine(file).FileExists;

    /// <summary>
    /// Which build a folder the user pointed at holds. A game that names its native executable is the native build
    /// only when that executable is in the folder; a game with a native build that names none is taken at its word.
    /// </summary>
    public static bool IsNativeBuildIn(this IGameData game, AbsolutePath installFolder)
        => game.HasNativeLinuxBuild && (game.NativeLinuxPrimaryFile is null || game.IsInstalledAt(installFolder));
}
