using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton;

/// <summary>
/// Tells which processes are ours, so nothing that goes looking for a game's processes signals or waits on them.
/// </summary>
/// <remarks>
/// Run from an AppImage, we are two processes: this one and the runtime's FUSE helper that serves the mount we run
/// from. The helper is reparented to the user's service manager, and when we were started from a Steam launch option
/// it inherited the game's <c>STEAM_COMPAT_DATA_PATH</c>, so <see cref="IProcessTracker.FindGameProcesses"/> lists it
/// as a game process. Ending it drops the mount and kills us on the next page we need from it.
/// </remarks>
internal sealed class OwnProcessFamily
{
    private const string DeletedSuffix = " (deleted)";
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly Func<int, string?> _readExeLink;
    private readonly int _ownPid;
    private readonly Lazy<string?> _appImage;

    /// <param name="getEnvironmentVariable">Reads an environment variable of this process.</param>
    /// <param name="readExeLink">Gives the text of <c>/proc/&lt;pid&gt;/exe</c>, or null when it cannot be read.</param>
    /// <param name="ownPid">This process's id.</param>
    public OwnProcessFamily(Func<string, string?> getEnvironmentVariable, Func<int, string?> readExeLink, int ownPid)
    {
        _getEnvironmentVariable = getEnvironmentVariable;
        _readExeLink = readExeLink;
        _ownPid = ownPid;
        _appImage = new Lazy<string?>(ResolveAppImage);
    }

    /// <summary>The family of the running process, read from its environment and <c>/proc</c>.</summary>
    public static OwnProcessFamily Current { get; } = new(Environment.GetEnvironmentVariable, ReadExeLink, Environment.ProcessId);

    /// <summary>Whether <paramref name="pid"/> is this process or its AppImage's mount helper.</summary>
    /// <remarks>
    /// The helper is found by its executable: <c>/proc/&lt;pid&gt;/exe</c> names the AppImage file. The link text is
    /// compared as a string, minus the <c>" (deleted)"</c> the kernel appends once a self-update has replaced the file,
    /// so a helper that mounted the previous version still matches. Without <c>APPIMAGE</c> only our own pid counts.
    /// </remarks>
    public bool Contains(int pid)
    {
        if (pid == _ownPid) return true;
        if (_appImage.Value is not { } appImage) return false;

        var exe = _readExeLink(pid);
        if (exe is null) return false;
        if (exe.EndsWith(DeletedSuffix, StringComparison.Ordinal)) exe = exe[..^DeletedSuffix.Length];
        return exe == appImage;
    }

    private string? ResolveAppImage()
        => _getEnvironmentVariable("APPIMAGE") is { Length: > 0 } path ? ProcProcessTracker.RealPath(path) : null;

    /// <summary>
    /// The processes that are safe to signal and are not ours. <c>kill(2)</c> reads 0 as our process group and -1 as
    /// every process we may signal, and pid 1 is init, so those never count as a process to signal either.
    /// </summary>
    public IReadOnlyList<TrackedProcess> Foreign(IEnumerable<TrackedProcess> processes)
        => processes.Where(process => process.Pid > 1 && !Contains(process.Pid)).ToList();

    private static string? ReadExeLink(int pid)
    {
        try { return new FileInfo($"/proc/{pid}/exe").LinkTarget; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
