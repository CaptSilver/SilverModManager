using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Larian.Engine;

internal static class LarianBuild
{
    /// <summary>The game runs on the host rather than under Wine, so the Windows-only parts of a mod do not apply.</summary>
    public static bool IsNative(GameLocatorResult result) => result.TargetOS.IsLinux && result.WinePrefixPath is null;
}
