using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Games;

/// <summary>
/// The shape of a file that is a mod loader or a mod rather than something a store ships. A baseline taken
/// from a folder with no store file list uses it to avoid calling an earlier mod setup the game's own files.
/// </summary>
public static class ModLoaderPaths
{
    /// <summary>
    /// DLL names a mod loader loads under by sitting next to the game's executable, because the game looks the
    /// real library up by name in its own folder first.
    /// </summary>
    public static readonly IReadOnlyList<string> ProxyDllNames = ["dinput8.dll", "d3d11.dll", "dxgi.dll", "version.dll", "winmm.dll", "winhttp.dll"];

    private const string SourceFolder = "src";

    /// <summary>
    /// True when <paramref name="path"/> is code that hooks the game (<see cref="IGameNativeCode"/>), a proxy
    /// DLL in one of the game's executable folders, or anything under a top-level <c>src</c> folder.
    /// Names compare without regard to case: the proxy a player kept was spelled <c>DINPUT8.dll</c>.
    /// </summary>
    public static bool LooksLikeAMod(IGame game, GameInstallation installation, GamePath path)
    {
        if (game is IGameNativeCode native && native.HooksGameCode(path)) return true;
        if (IsUnderTopLevelSource(path)) return true;
        return IsProxyDllBesideTheExecutable(game, installation, path);
    }

    private static bool IsUnderTopLevelSource(GamePath path)
    {
        // A bare file called src at the top is not a source tree.
        if (path.Path.Parent == RelativePath.Empty) return false;
        return string.Equals(path.Parts.FirstOrDefault().ToString(), SourceFolder, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProxyDllBesideTheExecutable(IGame game, GameInstallation installation, GamePath path)
    {
        var name = path.FileName.ToString();
        if (!ProxyDllNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        return game.ExecutableFolders(installation).Contains(path.Parent);
    }
}
