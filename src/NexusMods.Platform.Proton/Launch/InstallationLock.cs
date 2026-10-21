using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace NexusMods.Platform.Proton.Launch;

/// <summary>
/// Serialises launches of one game installation across processes with an advisory lock on
/// <c>launch-&lt;store&gt;-&lt;storeid&gt;.lock</c>, and describes the holder in a <c>.state</c> file next to it.
/// </summary>
/// <remarks>
/// The lock is taken with flock(2) on a descriptor we open ourselves. A <see cref="FileStream"/> can't be used
/// for it: .NET flocks every FileStream it opens, and opens it without regard for what the lock should mean.
/// The descriptor is O_CLOEXEC so a game (or a wrapper) started by the holder doesn't inherit it. An inherited
/// lock descriptor would keep the installation locked until the game exits, even after the holder has died.
/// The kernel drops the lock when the last descriptor on the open file closes, so a crashed holder can't leave
/// the lock stuck. Only the state file can be left over.
/// </remarks>
public sealed partial class InstallationLock : IDisposable
{
    /// <summary>The state file key holding the game's required Wine DLL overrides.</summary>
    public const string WineDllOverridesKey = "WineDllOverrides";

    private static readonly TimeSpan DefaultRetryPause = TimeSpan.FromMilliseconds(100);
    private const int MaxRenameAttempts = 5;

    // Shell scripts and the support bundle read the state file as well; a BOM would end up glued to the first key.
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private int _fd = -1;

    /// <summary>
    /// Creates a lock on the file at <paramref name="lockPath"/>. Nothing is opened until it is used.
    /// </summary>
    public InstallationLock(string lockPath)
    {
        LockPath = lockPath;
        StatePath = Path.ChangeExtension(lockPath, ".state");
    }

    /// <summary>
    /// The lock file for an installation. <paramref name="runtimeDir"/> is the user's runtime directory
    /// (<c>XDG_RUNTIME_DIR</c>), passed in so tests never touch the real one.
    /// </summary>
    public static string PathFor(string runtimeDir, string store, string storeId) =>
        Path.Combine(DirectoryFor(runtimeDir), $"launch-{Sanitise(store)}-{Sanitise(storeId)}.lock");

    /// <summary>
    /// The folder under <paramref name="runtimeDir"/> that holds every installation's lock and state files.
    /// </summary>
    public static string DirectoryFor(string runtimeDir) => Path.Combine(runtimeDir, "silvermod");

    public string LockPath { get; }

    public string StatePath { get; }

    /// <summary>
    /// True when this instance took the lock and has not released it.
    /// </summary>
    public bool IsHeld => _fd >= 0;

    /// <summary>
    /// Takes the lock without waiting. When another process has it, tries once more after
    /// <paramref name="retryPause"/> (default 100 ms): <see cref="Probe"/> holds the lock for an instant, and
    /// losing a launch to someone merely checking would be a spurious refusal. A successful acquire discards
    /// the state file of whoever held the lock before.
    /// </summary>
    public bool TryAcquire(TimeSpan? retryPause = null)
    {
        if (IsHeld) return true;

        var fd = TryFlock();
        if (fd < 0)
        {
            Thread.Sleep(retryPause ?? DefaultRetryPause);
            fd = TryFlock();
            if (fd < 0) return false;
        }

        _fd = fd;
        File.Delete(StatePath);
        return true;
    }

    /// <summary>
    /// Whether another holder has the lock right now. Takes the lock and releases it again when it is free.
    /// </summary>
    public bool Probe()
    {
        var fd = TryFlock();
        if (fd < 0) return true;
        _ = close(fd);
        return false;
    }

    /// <summary>
    /// Records the holder in the state file, keeping any keys the wrapper has added.
    /// </summary>
    /// <param name="wineDllOverrides">
    /// The game's required overrides as a <c>WINEDLLOVERRIDES</c> value, empty for none. Written on every call so
    /// an earlier launch's value never outlives it.
    /// </param>
    public void WriteState(int pid, string loadout, LaunchPhase phase, string wineDllOverrides) => Rewrite(values =>
    {
        values["pid"] = pid.ToString(CultureInfo.InvariantCulture);
        values["loadout"] = SingleLine(loadout);
        values["phase"] = phase.ToString().ToLowerInvariant();
        values[WineDllOverridesKey] = SingleLine(wineDllOverrides);
    });

    /// <summary>
    /// Adds or replaces one key in the state file. Callable by a process that doesn't hold the lock (the Steam
    /// wrapper reports its pid this way), so the file is re-read right before it is swapped in: whatever the
    /// holder wrote meanwhile, the pid, loadout and phase on disk at that moment are what survive.
    /// </summary>
    public void WriteWrapperKey(string key, string value)
    {
        if (key.Length == 0 || key.AsSpan().ContainsAny("=\r\n"))
            throw new ArgumentException($"Invalid state key '{key}'", nameof(key));
        Rewrite(values => values[key] = SingleLine(value));
    }

    /// <summary>
    /// The state file, or null when there isn't one. <see cref="InstallationLockState.IsLeftover"/> is set when
    /// nobody holds the lock, meaning the previous holder died without cleaning up.
    /// </summary>
    public InstallationLockState? ReadState()
    {
        var values = ReadValues(out _);
        if (values is null) return null;

        if (!values.TryGetValue("pid", out var pid) || !int.TryParse(pid, CultureInfo.InvariantCulture, out var pidValue))
            throw new InvalidDataException($"State file {StatePath} has no valid pid");
        if (!values.TryGetValue("phase", out var phase) || !Enum.TryParse<LaunchPhase>(phase, ignoreCase: true, out var phaseValue))
            throw new InvalidDataException($"State file {StatePath} has no valid phase");

        return new InstallationLockState(pidValue, values.GetValueOrDefault("loadout", ""), phaseValue, IsLeftover: !IsHeld && !Probe(), values);
    }

    /// <summary>
    /// Releases the lock if this instance holds it.
    /// </summary>
    public void Dispose()
    {
        var fd = Interlocked.Exchange(ref _fd, -1);
        if (fd >= 0) _ = close(fd);
    }

    private int TryFlock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);

        var fd = open(LockPath, O_RDWR | O_CREAT | O_CLOEXEC, 0b110_000_000);
        if (fd < 0) throw new IOException($"Cannot open {LockPath}", new Win32Exception(Marshal.GetLastPInvokeError()));

        if (flock(fd, LOCK_EX | LOCK_NB) == 0) return fd;

        var errno = Marshal.GetLastPInvokeError();
        _ = close(fd);
        if (errno == EWOULDBLOCK) return -1;
        throw new IOException($"Cannot lock {LockPath}", new Win32Exception(errno));
    }

    /// <summary>
    /// Applies <paramref name="change"/> to the state file's current contents and swaps the result in. The
    /// file is read again just before the rename; if it moved, the change is reapplied to the newer contents.
    /// </summary>
    private void Rewrite(Action<Dictionary<string, string>> change)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        var temp = $"{StatePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                var values = ReadValues(out var before) ?? new Dictionary<string, string>();
                change(values);
                File.WriteAllText(temp, Serialise(values), Utf8NoBom);

                if (ReadText() == before || attempt == MaxRenameAttempts)
                {
                    File.Move(temp, StatePath, overwrite: true);
                    return;
                }
            }
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private string? ReadText()
    {
        // A new holder deletes the previous state file on acquire, which can land between a check and the read.
        try
        {
            return File.ReadAllText(StatePath);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private Dictionary<string, string>? ReadValues(out string? text)
    {
        text = ReadText();
        if (text is null) return null;

        var values = new Dictionary<string, string>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = line.IndexOf('=');
            if (split <= 0) throw new InvalidDataException($"Malformed line '{line}' in {StatePath}");
            values[line[..split]] = line[(split + 1)..].TrimEnd('\r');
        }
        return values;
    }

    private static string Serialise(Dictionary<string, string> values)
    {
        var builder = new StringBuilder();
        foreach (var (key, value) in values) builder.Append(key).Append('=').Append(value).Append('\n');
        return builder.ToString();
    }

    private static string SingleLine(string value) => value.ReplaceLineEndings(" ");

    private static string Sanitise(string part) =>
        string.Create(part.Length, part, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
                span[i] = char.IsAsciiLetterOrDigit(source[i]) || source[i] is '.' or '-' or '_' ? source[i] : '_';
        });

    private const int O_RDWR = 2;
    private const int O_CREAT = 0x40;
    private const int O_CLOEXEC = 0x80000;
    private const int LOCK_EX = 2;
    private const int LOCK_NB = 4;
    private const int EWOULDBLOCK = 11;

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int open(string path, int flags, uint mode);

    [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static partial int flock(int fd, int operation);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int close(int fd);
}
