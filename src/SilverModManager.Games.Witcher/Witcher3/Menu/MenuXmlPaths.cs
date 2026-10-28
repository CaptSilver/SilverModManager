using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Witcher.Witcher3.Menu;

/// <summary>Reads the menu config folder, where each mod's menu XML sits next to the game's own.</summary>
internal static class MenuXmlPaths
{
    /// <summary>The name without the extension of a <c>.xml</c> file directly in the menu config folder, or null for any other path.</summary>
    public static string? NameOf(GamePath path)
    {
        if (path.Parent != WitcherConstants.W3MenuConfigFolder) return null;

        var file = path.FileName.ToString();
        return file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? file[..^".xml".Length] : null;
    }

    public static bool IsNative(string name) =>
        WitcherConstants.W3NativeMenuXmls.Contains(name, StringComparer.OrdinalIgnoreCase);
}
