using System.Diagnostics;
using System.Text;
using CliWrap;
using LinuxDesktopUtils.XDGDesktopPortal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Backend.RuntimeDependency;
using NexusMods.Paths;
using SilverModManager.Sdk;

namespace SilverModManager.Backend.OS;

internal partial class LinuxInterop : IOSInterop
{
    private readonly ILogger _logger;
    private readonly IFileSystem _fileSystem;
    private readonly IProcessRunner _processRunner;

    private readonly DesktopPortalConnectionManagerWrapper _portalWrapper;
    private readonly XdgSettingsDependency _xdgSettingsDependency;
    private readonly UpdateDesktopDatabaseDependency _updateDesktopDatabaseDependency;

    private readonly Func<Uri, Task<ResponseStatus>> _portalOpenUri;

    public event Action<Uri>? OpenUriFailed;

    public LinuxInterop(IServiceProvider serviceProvider) : this(serviceProvider, portalOpenUri: null) { }

    /// <param name="portalOpenUri">Replaces the desktop portal call, so tests never reach D-Bus.</param>
    internal LinuxInterop(IServiceProvider serviceProvider, Func<Uri, Task<ResponseStatus>>? portalOpenUri)
    {
        _portalOpenUri = portalOpenUri ?? OpenUriWithPortal;
        _logger = serviceProvider.GetRequiredService<ILogger<LinuxInterop>>();
        _processRunner = serviceProvider.GetRequiredService<IProcessRunner>();
        _fileSystem = serviceProvider.GetRequiredService<IFileSystem>();

        _portalWrapper = new DesktopPortalConnectionManagerWrapper();
        _xdgSettingsDependency = serviceProvider.GetRequiredService<XdgSettingsDependency>();
        _updateDesktopDatabaseDependency = serviceProvider.GetRequiredService<UpdateDesktopDatabaseDependency>();
    }

    public AbsolutePath GetRunningExecutablePath(out string rawPath)
    {
        // https://docs.appimage.org/packaging-guide/environment-variables.html#type-2-appimage-runtime
        // APPIMAGE: (Absolute) path to AppImage file (with symlinks resolved)
        var appImagePath = Environment.GetEnvironmentVariable("APPIMAGE", EnvironmentVariableTarget.Process);
        if (appImagePath is not null)
        {
            rawPath = appImagePath;
            return _fileSystem.FromUnsanitizedFullPath(rawPath);
        }

        rawPath = Environment.ProcessPath ?? throw new NotSupportedException("Unable to get process path");
        return _fileSystem.FromUnsanitizedFullPath(rawPath);
    }

    private async ValueTask<OpenUriPortal> GetPortal()
    {
        var connectionManager = await _portalWrapper.GetInstance();
        var portal = await connectionManager.GetOpenUriPortalAsync();
        return portal;
    }

    public void OpenUri(Uri uri)
    {
        Debug.Assert(!uri.IsFile, $"use {nameof(OpenFile)} for opening file `{uri}`");
        OpenUriAsync(uri).FireAndForget(_logger);
    }

    public void OpenFile(AbsolutePath filePath)
    {
        if (!Helper.AssertIsFile(filePath, _logger)) return;
        OpenFileImpl(filePath).FireAndForget(_logger);
    }

    public void OpenDirectory(AbsolutePath directoryPath)
    {
        if (!Helper.AssertIsDirectory(directoryPath, _logger)) return;

        // NOTE(erri120): the XDG Desktop Portal API works off file descriptors
        // we can't open empty directories, we need at least one file

        if (!directoryPath.EnumerateFiles().TryGetFirst(out var file))
        {
            _logger.LogWarning("Opening empty directories is not supported on Linux");
            return;
        }

        OpenFileInDirectoryImpl(file).FireAndForget(_logger);
    }

    public void OpenFileInDirectory(AbsolutePath filePath)
    {
        OpenFileInDirectoryImpl(filePath).FireAndForget(_logger);
    }

    private async Task<ResponseStatus> OpenUriWithPortal(Uri uri)
    {
        var portal = await GetPortal();
        var response = await portal.OpenUriAsync(uri);
        return response.Status;
    }

    /// <summary>
    /// Tries the desktop portal, then <c>xdg-open</c>, and raises <see cref="OpenUriFailed"/> when neither opened it.
    /// </summary>
    internal async Task OpenUriAsync(Uri uri)
    {
        try
        {
            var status = await _portalOpenUri(uri);
            if (status == ResponseStatus.Success) return;

            // Closing the portal's app chooser is the user's answer, not a failure to recover from.
            if (status == ResponseStatus.UserCancelled)
            {
                _logger.LogInformation("Opening `{Uri}` was cancelled in the desktop portal", uri.AbsoluteUri);
                return;
            }

            // An aborted request comes back as a normal response, so without this it ended in nothing and no log line.
            _logger.LogError("The desktop portal answered {Status} for `{Uri}`, trying xdg-open", status, uri.AbsoluteUri);
        }
        catch (Exception e)
        {
            // Inside an AppImage the session bus variables are the first suspect, so they go in the log.
            _logger.LogError(e, "The desktop portal failed to open `{Uri}`, trying xdg-open (DBUS_SESSION_BUS_ADDRESS={DBusAddress}, XDG_RUNTIME_DIR={RuntimeDir}, APPIMAGE={AppImage})",
                uri.AbsoluteUri,
                Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") ?? "<unset>",
                Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "<unset>",
                Environment.GetEnvironmentVariable("APPIMAGE") ?? "<unset>");
        }

        try
        {
            var command = Cli.Wrap("xdg-open")
                .WithArguments([uri.AbsoluteUri])
                .WithValidation(CommandResultValidation.None);

            var result = await _processRunner.RunAsync(command, logOutput: true);
            if (result.ExitCode == 0) return;

            _logger.LogError("xdg-open exited with code {ExitCode} for `{Uri}`", result.ExitCode, uri.AbsoluteUri);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "xdg-open could not be run for `{Uri}`", uri.AbsoluteUri);
        }

        OpenUriFailed?.Invoke(uri);
    }
 
    private async Task OpenFileImpl(AbsolutePath filePath)
    {
        var portal = await GetPortal();
        await portal.OpenFileAsync(file: FilePath.From(filePath.ToNativeSeparators(_fileSystem.OS)));
    }

    private async Task OpenFileInDirectoryImpl(AbsolutePath filePath)
    {
        var portal = await GetPortal();
        await portal.OpenFileInDirectoryAsync(file: FilePath.From(filePath.ToNativeSeparators(_fileSystem.OS)));
    }

    public async ValueTask<FileSystemMount[]> GetFileSystemMounts(CancellationToken cancellationToken = default)
    {
        var stdOut = new StringBuilder();
        var command = Cli.Wrap("df").WithArguments([
            "--local",                                          // limit listing to local file systems
            "--output=source,fstype,size,avail,target",         // output these columns
            "--block-size=1K",                                  // scale sizes
            "--exclude-type=tmpfs",                             // limit listing to file systems not of this type
            "--exclude-type=devtmpfs",
            "--exclude-type=vfat",
            "--exclude-type=efivarfs",
        ]).WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdOut));

        await _processRunner.RunAsync(command, logOutput: true, cancellationToken: cancellationToken);
        return ParseFileSystemMounts(_fileSystem, stdOut.ToString());
    }

    public async ValueTask<FileSystemMount?> GetFileSystemMount(AbsolutePath path, CancellationToken cancellationToken = default)
    {
        var stdOut = new StringBuilder();
        var command = Cli.Wrap("df").WithArguments([
            NearestExistingDirectory(path).ToNativeSeparators(_fileSystem.OS),
            "--output=source",
        ]).WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdOut));

        await _processRunner.RunAsync(command, logOutput: true, cancellationToken: cancellationToken);

        var knownFileSystemMounts = await GetFileSystemMounts(cancellationToken: cancellationToken);
        return ParseFileSystemMount(knownFileSystemMounts, stdOut.ToString());
    }

    // df exits 1 on a path that doesn't exist, which a folder the app creates later (the archive
    // folder on a first run) doesn't yet. It will land on the same mount as its parent.
    internal static AbsolutePath NearestExistingDirectory(AbsolutePath path)
    {
        var current = path;
        while (!current.DirectoryExists() && current != current.GetRootComponent)
        {
            current = current.Parent;
        }

        return current;
    }

    internal static FileSystemMount? ParseFileSystemMount(IReadOnlyList<FileSystemMount> knownFileSystemMounts, ReadOnlySpan<char> stdOut)
    {
        var lineEnumerator = stdOut.EnumerateLines();

        // skip header
        if (!lineEnumerator.MoveNext()) return null;
        if (!lineEnumerator.MoveNext()) return null;

        var source = lineEnumerator.Current.ToString();
        return knownFileSystemMounts.FirstOrDefault(x => x.Source.Equals(source, StringComparison.OrdinalIgnoreCase));
    }

    internal static FileSystemMount[] ParseFileSystemMounts(IFileSystem fileSystem, ReadOnlySpan<char> stdOut)
    {
        var lineEnumerator = stdOut.EnumerateLines();

        // skip header
        if (!lineEnumerator.MoveNext()) return [];

        var tmp = new List<FileSystemMount>();

        foreach (var line in lineEnumerator)
        {
            if (line.IsWhiteSpace()) continue;

            var splitEnumerator = line.Split(' ');
            var index = 0;

            var source = string.Empty;
            var type = string.Empty;
            var bytesTotal = Size.Zero;
            var bytesAvailable = Size.Zero;
            var target = default(AbsolutePath);

            foreach (var split in splitEnumerator)
            {
                var span = line[split];
                if (span.IsWhiteSpace()) continue;

                if (index == 0)
                {
                    source = span.ToString();
                }
                else if (index == 1)
                {
                    type = span.ToString();
                } else if (index == 2)
                {
                    bytesTotal = BlocksToSize(span);
                } else if (index == 3)
                {
                    bytesAvailable = BlocksToSize(span);
                } else if (index == 4)
                {
                    target = fileSystem.FromUnsanitizedFullPath(span.ToString());
                }

                index += 1;
            }

            tmp.Add(new FileSystemMount(
                Source: source,
                Target: target,
                Type: type,
                BytesTotal: bytesTotal,
                BytesAvailable: bytesAvailable
            ));
        }

        // NOTE(erri120): Need to group by source since one source can have multiple mounting points.
        var results = tmp
            .GroupBy(mount => mount.Source)
            .Select(groups => groups
                .OrderBy(mount => mount.Target.Parts.Count())
                .First()
            )
            .ToArray();

        return results;

        static Size BlocksToSize(ReadOnlySpan<char> input)
        {
            if (!ulong.TryParse(input, out var blocks)) return Size.Zero;
            return Size.From(Size.KB.Value * blocks);
        }
    }
}

internal sealed class DesktopPortalConnectionManagerWrapper : IAsyncDisposable
{
    private readonly SemaphoreSlim _semaphoreSlim = new(initialCount: 1, maxCount: 1);
    private readonly Func<Task<DesktopPortalConnectionManager>> _connect;
    private readonly TimeSpan _connectTimeout;
    private DesktopPortalConnectionManager? _instance;

    public DesktopPortalConnectionManagerWrapper() : this(connect: null, connectTimeout: TimeSpan.FromSeconds(10)) { }

    /// <param name="connect">Replaces the D-Bus connect, so tests can make it fail.</param>
    /// <param name="connectTimeout">How long a call waits for another call's connect to finish.</param>
    internal DesktopPortalConnectionManagerWrapper(Func<Task<DesktopPortalConnectionManager>>? connect, TimeSpan connectTimeout)
    {
        _connect = connect ?? (async () => await DesktopPortalConnectionManager.ConnectAsync());
        _connectTimeout = connectTimeout;
    }

    private async ValueTask<DesktopPortalConnectionManager> InitAsync()
    {
        // Carrying on without the semaphore would release one this call never took, overflowing it for the holder.
        if (!await _semaphoreSlim.WaitAsync(timeout: _connectTimeout))
            throw new TimeoutException($"Timed out after {_connectTimeout} waiting for another call to connect to the desktop portal");

        try
        {
            // A call that waited on the semaphore finds the connection its predecessor made.
            if (_instance is not null) return _instance;

            var manager = await _connect();
            _instance = manager;
            return manager;
        }
        finally
        {
            // Kept held, a failed connect made every later call wait out the full timeout.
            _semaphoreSlim.Release();
        }
    }

    public ValueTask<DesktopPortalConnectionManager> GetInstance()
    {
        if (_instance is not null) return new ValueTask<DesktopPortalConnectionManager>(_instance);
        return InitAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_instance is not null) await _instance.DisposeAsync();
    }
}

