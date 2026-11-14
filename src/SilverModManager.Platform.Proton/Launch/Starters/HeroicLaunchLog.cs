using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Launch.Starters;

/// <summary>Where Heroic writes the log of one game's launch, and the runner names it files that log under.</summary>
internal static class HeroicLaunchLog
{
    /// <summary>
    /// <c>&lt;XDG_STATE_HOME&gt;/Heroic/logs/games/&lt;app&gt;_&lt;runner&gt;/launch.log</c>, with <c>~/.local/state</c> when the
    /// variable is unset; the Flatpak keeps its own state folder under <c>~/.var/app</c> whatever the host sets.
    /// </summary>
    public static AbsolutePath PathFor(IFileSystem fileSystem, StoreClient client, string runner, string? xdgStateHome = null)
    {
        var home = fileSystem.GetKnownPath(KnownPath.HomeDirectory);
        var state = client.IsFlatpak
            ? home.Combine(".var/app/com.heroicgameslauncher.hgl/.local/state")
            : string.IsNullOrEmpty(xdgStateHome) || !System.IO.Path.IsPathRooted(xdgStateHome)
                ? home.Combine(".local/state")
                : fileSystem.FromUnsanitizedFullPath(xdgStateHome);
        return state.Combine($"Heroic/logs/games/{client.AppName}_{runner}/launch.log");
    }

    /// <summary>The runner Heroic keeps a store's games under.</summary>
    public static string RunnerOf(GameStore store)
    {
        if (store == GameStore.EGS) return "legendary";
        if (store == GameStore.GOG) return "gog";
        if (store == GameStore.Amazon) return "nile";
        throw new ArgumentOutOfRangeException(nameof(store), store, "Heroic has no runner for this store");
    }
}
