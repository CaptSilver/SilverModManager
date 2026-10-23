using System.Globalization;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;

namespace NexusMods.Platform.Proton.Launch;

/// <summary>
/// Recognises a Steam launch option that fires while the window is already playing the same game. Steam
/// runs the wrapper for a game the window started through Steam itself; the window owns the launch, so the
/// wrapper only reports itself and starts what Steam handed it, with the exe and arguments the window recorded
/// put in place.
/// </summary>
public static class WrapperPassThrough
{
    // The same lock name LaunchSession takes for a Steam installation.
    private static readonly string SteamStore = GameStore.Steam.Value.ToLowerInvariant();

    /// <summary>
    /// Runs <paramref name="command"/> when the window with pid <paramref name="guiPid"/> is running
    /// a launch of the game it belongs to, and records this process as the launch's wrapper. Returns the
    /// command's exit code, or null without starting anything when there is no such launch. A launch that
    /// recorded an exe and arguments has them applied to the command the way a <c>run</c> launch applies them;
    /// when the command can't take them, nothing is started and the result is
    /// <see cref="LaunchSession.FailureExitCode"/>, because the game would otherwise run without its mods.
    /// </summary>
    /// <param name="baseEnvironment">The child's environment; null passes ours on.</param>
    /// <param name="notifier">Told why the command was refused; null for no desktop message.</param>
    public static async Task<int?> TryRunAsync(
        IReadOnlyList<string> command,
        IEnvironmentReader environment,
        int guiPid,
        ILogger logger,
        IReadOnlyDictionary<string, string>? baseEnvironment = null,
        IDesktopNotifier? notifier = null)
    {
        var joined = TryOpen(command, environment.Get("SteamAppId"), environment.Get("XDG_RUNTIME_DIR"), guiPid);
        if (joined is null) return null;

        var (launch, state) = joined.Value;
        using var _ = launch;
        IReadOnlyList<string> argv;
        try
        {
            argv = state.RecordedLaunch is { } recorded ? Substitute(command, recorded) : command;
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException)
        {
            // Same report a run launch gives: log, stderr and the desktop.
            logger.LogError(e, "Cannot apply the loadout's launch to Steam's command");
            Console.Error.WriteLine(e.Message);
            notifier?.Notify(ApplicationConstants.ProductName, e.Message);
            return LaunchSession.FailureExitCode;
        }

        // The wrapper has no database to ask which overrides the game needs; the window recorded them in the state file.
        // The window waits on Steam for as long as the key is there, so it is only written once the game is up.
        using var process = GameProcess.PassThrough(new GameProcessSpec(argv, WineDllOverrides: state.WineDllOverrides), baseEnvironment);
        try
        {
            launch.WriteWrapperKey(LaunchSession.WrapperKey, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Steam treats the wrapper's exit as the game's, so the game it already started is still waited on.
            logger.LogWarning(e, "Could not record the Steam wrapper in {StatePath}", launch.StatePath);
        }

        return (await process.WaitAsync()).ExitCode;
    }

    private static IReadOnlyList<string> Substitute(IReadOnlyList<string> command, RecordedLaunch recorded)
    {
        var root = FileSystem.Shared.FromUnsanitizedFullPath(recorded.GameRoot);
        var exe = root.Combine(recorded.Exe);
        var spec = new LaunchSpec(exe, [..recorded.ExtraArgs]);
        // Only Steam starts the game for a recorded launch, and it starts the game's own exe, so the recorded exe stands
        // for the primary one: what the substitution needs is a slot for the arguments.
        return LaunchPlanner.Compose(command, root, exe, spec, filesystemRoots: [], wineDllOverrides: []).Process.Argv;
    }

    /// <summary>
    /// The lock for the installation <paramref name="command"/> belongs to, when the window with pid
    /// <paramref name="guiPid"/> holds it in the running phase. The app id comes from <paramref name="steamAppId"/>
    /// (Steam's <c>SteamAppId</c>) and, failing that, from <c>AppId=</c> in the command. Null in every other
    /// case, including a lock nobody holds: that state file is a dead launch's leftover.
    /// The returned lock is not held; the caller disposes it. The state is the one that was checked, so the caller
    /// need not read the file a second time.
    /// </summary>
    public static (InstallationLock Lock, InstallationLockState State)? TryOpen(IReadOnlyList<string> command, string? steamAppId, string? runtimeDir, int guiPid)
    {
        if (string.IsNullOrEmpty(runtimeDir)) return null;

        var appId = steamAppId?.Trim();
        if (string.IsNullOrEmpty(appId)) appId = ExeSubstitution.ExtractAppId(command);
        if (string.IsNullOrEmpty(appId)) return null;

        var path = InstallationLock.PathFor(runtimeDir, SteamStore, appId);
        if (!File.Exists(Path.ChangeExtension(path, ".state"))) return null;

        var installationLock = new InstallationLock(path);
        try
        {
            var state = installationLock.ReadState();
            if (state is { IsLeftover: false, Phase: LaunchPhase.Running } && state.Pid == guiPid) return (installationLock, state);
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            // A half-written or foreign state file is not a launch we can join.
        }

        installationLock.Dispose();
        return null;
    }
}
