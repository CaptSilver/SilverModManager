using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using NexusMods.Sdk.Settings;

namespace NexusMods.SingleProcess;

/// <summary>
/// A shared file that is used to synchronize the main process and client processes.
/// </summary>
public class SyncFile
{
    // Slot 0 layout: pid in the high 32 bits, kind in bits 16..31, port in bits 0..15.
    private const int KindShift = 16;
    private const ulong PortMask = 0xFFFF;

    private readonly MultiProcessSharedArray _sharedArray;
    private readonly ILogger<SyncFile> _logger;

    /// <summary>
    /// DI constructor
    /// </summary>
    public SyncFile(ILogger<SyncFile> logger, ISettingsManager settingsManager)
        : this(logger, settingsManager.Get<CliSettings>().SyncFile) { }

    internal SyncFile(ILogger<SyncFile> logger, AbsolutePath path)
    {
        _logger = logger;
        _logger.LogDebug("Using Sync File: {SyncFile}", path);
        _sharedArray = new MultiProcessSharedArray(path, itemCount: 8);
    }

    /// <summary>
    /// The raw slot 0 word, for tests that need to plant or inspect it.
    /// </summary>
    internal ulong RawMainWord => _sharedArray.Get(0);
    
    /// <summary>
    /// True if the main process is running
    /// </summary>
    public bool IsMainRunning => GetSyncInfo().Process is not null;
    
    /// <summary>
    /// Returns the current process, its kind and its port that's stored in the sync file
    /// </summary>
    public (Process? Process, MainKind Kind, int Port) GetSyncInfo()
    {
        var rawValue = _sharedArray.Get(0);
        var processId = (int)(rawValue >> 32);
        var kind = (MainKind)((rawValue >> KindShift) & 0xFFFF);
        var port = (int)(rawValue & PortMask);

        _logger.LogDebug("Sync file contents: {Port}, {Kind}, {ProcessId}", port, kind, processId);

        // A dead pid means no main, whatever kind it left behind.
        if (processId == 0) return (null, kind, port);
        return (GetProcessById(processId), kind, port);
    }

    /// <summary>
    /// Flags this process as the main process, returns false if another process is already the main process
    /// </summary>
    public bool TrySetMain(MainKind kind, int port)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, (int)PortMask);

        var newProcessId = Environment.ProcessId;
        var newContents = ((ulong)newProcessId << 32) | ((ulong)kind << KindShift) | (uint)port;

        var currentRawValue = _sharedArray.Get(0);
        var currentProcessId = (int)(currentRawValue >> 32);

        var process = GetProcessById(currentProcessId);
        if (process is not null && process.Id != newProcessId)
            return false;

        return _sharedArray.CompareAndSwap(0, currentRawValue, newContents);
    }

    /// <summary>
    /// Clears the main process entry, but only if it still holds this process's id.
    /// </summary>
    public bool ReleaseMain()
    {
        var currentRawValue = _sharedArray.Get(0);
        if ((int)(currentRawValue >> 32) != Environment.ProcessId) return false;

        // The CAS fails if another process claimed the slot between the read and here.
        return _sharedArray.CompareAndSwap(0, currentRawValue, 0);
    }

    /// <summary>
    /// Returns the process with the given id, or null if it doesn't exist
    /// </summary>
    private static Process? GetProcessById(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            return process.Id == 0 ? null : process;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

