using NexusMods.Platform.Proton.Launch;
using NexusMods.SingleProcess;
using NexusMods.SingleProcess.Exceptions;

namespace NexusMods.App;

/// <summary>
/// Words the refusal for a start that runs into a live headless main. A <c>run</c> main can say which loadout
/// it is launching and how far it got, because it records that in the state file next to its launch lock.
/// </summary>
public static class HeadlessMainRefusal
{
    /// <summary>
    /// The refusal for the headless main <paramref name="pid"/> of <paramref name="kind"/>. The loadout and
    /// phase come from the <c>launch-*.state</c> under <paramref name="runtimeDir"/> that names the same pid;
    /// with none, the message names the pid alone.
    /// </summary>
    public static string Describe(MainKind kind, int pid, string? runtimeDir)
    {
        if (kind == MainKind.HeadlessRun && FindState(pid, runtimeDir) is { } state)
        {
            var launch = $"(pid {pid}, loadout '{state.Loadout}')";
            return state.Phase switch
            {
                LaunchPhase.Preparing => $"A game is starting through SilverModManager {launch}. Close it once it is up, then try again.",
                LaunchPhase.Finishing => $"A game launched through SilverModManager {launch} has exited and is still cleaning up. Try again in a moment.",
                _ => $"A game is running through SilverModManager {launch}. Close the game and try again.",
            };
        }

        return new HeadlessMainRunning(kind, pid).Message;
    }

    private static InstallationLockState? FindState(int pid, string? runtimeDir)
    {
        if (string.IsNullOrEmpty(runtimeDir)) return null;

        var folder = InstallationLock.DirectoryFor(runtimeDir);
        if (!Directory.Exists(folder)) return null;

        foreach (var path in Directory.EnumerateFiles(folder, "launch-*.lock"))
        {
            try
            {
                var state = new InstallationLock(path).ReadState();
                if (state?.Pid == pid) return state;
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                // A half-written or foreign state file only costs the details.
            }
        }

        return null;
    }
}
