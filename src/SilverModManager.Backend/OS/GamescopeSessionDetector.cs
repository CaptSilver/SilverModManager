using Microsoft.Extensions.Logging;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Backend.OS;

/// <summary>
/// Detects a gamescope Game Mode session from the environment and from the process ancestry. The answer is worked
/// out once: a session does not turn into another kind while the app runs.
/// </summary>
public sealed class GamescopeSessionDetector : IGameModeDetector
{
    /// <inheritdoc />
    public bool IsGameMode => Reason is not null;

    /// <inheritdoc />
    public string? Reason { get; }

    public GamescopeSessionDetector(IEnvironmentReader environment, IProcessTracker processes, int ownPid, ILogger<GamescopeSessionDetector>? logger = null)
    {
        Reason = Detect(environment, processes, ownPid);
        logger?.LogInformation("Game Mode: {IsGameMode} ({Reason})", IsGameMode, Reason ?? "no gamescope signal");
    }

    private static string? Detect(IEnvironmentReader environment, IProcessTracker processes, int ownPid)
    {
        if (string.Equals(environment.Get("XDG_CURRENT_DESKTOP"), "gamescope", StringComparison.OrdinalIgnoreCase))
            return "XDG_CURRENT_DESKTOP is gamescope";

        if (HasSteamPalAncestor(processes, ownPid))
            return "an ancestor process is steam.sh -steampal";

        if (environment.Get("SteamOS") == "1" && environment.Get("SteamGamepadUI") == "1")
            return "SteamOS=1 and SteamGamepadUI=1";

        return null;
    }

    // The walk ends at init (pid 1), at a vanished process and on a repeated pid, so a broken /proc cannot loop it.
    private static bool HasSteamPalAncestor(IProcessTracker processes, int ownPid)
    {
        var seen = new HashSet<int> { ownPid };
        var pid = ownPid;
        while (processes.ParentOf(pid) is > 1 and var parent && seen.Add(parent))
        {
            pid = parent;
            var arguments = processes.ArgumentsOf(pid);
            if (arguments.Any(a => a.EndsWith("steam.sh", StringComparison.Ordinal)) && arguments.Contains("-steampal"))
                return true;
        }
        return false;
    }
}
