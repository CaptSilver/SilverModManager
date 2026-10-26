using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.PrefixSetup;

/// <summary>
/// Brings components such as the Visual C++ runtime into a game's existing Wine prefix, one step per
/// component. A run without <see cref="PrefixSetupRequest.Write"/> only reports what it would do.
/// </summary>
/// <remarks>
/// The prefix belongs to Steam, Heroic or Lutris, so the runner is conservative: it never creates a prefix, it
/// refuses while the game or any process bound to the prefix runs, it needs the user to name the prefix it
/// may change, and it copies the registry files into the app's own data folder first. A component is logged
/// in <c>winetricks.log</c> only after the prefix shows it installed, because protontricks treats a logged verb
/// as done.
/// </remarks>
public sealed class PrefixSetupRunner : IPrefixSetupRunner
{
    private const string BackupFolder = "prefix-backups";
    private const string CacheFolder = "prefix-components";

    // Wine exposes only the low byte of a Windows exit code (1638 -> 102, 1641 -> 105, 3010 -> 194).
    private static readonly ImmutableHashSet<int> InstallerSuccessCodes = [0, 102, 105, 194, 1638, 1641, 3010];

    private readonly IPrefixResolver _resolver;
    private readonly IProcessTracker _processes;
    private readonly IInstallationLockFactory _locks;
    private readonly IToolRunner _tools;
    private readonly IPrefixInspector _inspector;
    private readonly IPrefixFileFetcher _fetcher;
    private readonly IFileSystem _fileSystem;
    private readonly TimeProvider _time;
    private readonly Func<PrefixComponentId, PrefixComponent> _component;

    /// <summary>
    /// Creates a runner over the built-in component table.
    /// </summary>
    public PrefixSetupRunner(
        IPrefixResolver resolver,
        IProcessTracker processes,
        IInstallationLockFactory locks,
        IToolRunner tools,
        IPrefixInspector inspector,
        IPrefixFileFetcher fetcher,
        IFileSystem fileSystem,
        TimeProvider time)
        : this(resolver, processes, locks, tools, inspector, fetcher, fileSystem, time, PrefixComponents.Get) { }

    internal PrefixSetupRunner(
        IPrefixResolver resolver,
        IProcessTracker processes,
        IInstallationLockFactory locks,
        IToolRunner tools,
        IPrefixInspector inspector,
        IPrefixFileFetcher fetcher,
        IFileSystem fileSystem,
        TimeProvider time,
        Func<PrefixComponentId, PrefixComponent> component)
    {
        _resolver = resolver;
        _processes = processes;
        _locks = locks;
        _tools = tools;
        _inspector = inspector;
        _fetcher = fetcher;
        _fileSystem = fileSystem;
        _time = time;
        _component = component;
    }

    /// <summary>
    /// Plans the setup and, for a write, carries it out. A step that fails after its attempts stops the run;
    /// the result says which and why, and the prefix keeps what earlier steps installed.
    /// </summary>
    /// <exception cref="PrefixSetupRefusedException">Nothing was changed: the prefix is missing, not ready or unsupported, the write is not confirmed, or something is running in it.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled; steps that completed stay installed.</exception>
    public async Task<PrefixSetupResult> Run(GameInstallation installation, PrefixSetupRequest request, CancellationToken cancellationToken, Func<PrefixSetupStepResult, ValueTask>? stepFinished = null)
    {
        var prefix = ResolvePrefix(installation);
        var steps = BuildSteps(prefix, request.Components);

        if (!request.Write) return new PrefixSetupResult(MakePlan(prefix, steps), Written: false, []);

        RequireConsent(prefix, request.ConfirmedPrefixPath);
        RequireNoRunningProcesses(installation, prefix);

        var store = installation.LocatorResult.Store.Value.ToLowerInvariant();
        using var installationLock = _locks.Create(store, installation.LocatorResult.StoreIdentifier);
        if (!installationLock.TryAcquire())
            throw new PrefixSetupRefusedException($"Another SilverModManager window is launching or changing this game ({installationLock.LockPath} is locked). Wait for it to finish, then try again.");

        // Detected under the lock, so the plan a write acts on is the one nothing else can change.
        var plan = MakePlan(prefix, steps);
        if (plan.BackupDirectory is { } backup) BackUpRegistry(prefix, backup);

        var results = new List<PrefixSetupStepResult>();
        for (var i = 0; i < steps.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await RunStep(installation, prefix, steps[i], plan.Steps[i], request.Attempts, cancellationToken);
            results.Add(result);
            if (stepFinished is not null) await stepFinished(result);
            if (result.Status != PrefixSetupStepStatus.Failed) continue;

            foreach (var skipped in steps.Skip(i + 1).Select(s => new PrefixSetupStepResult(s.Id, s.Name, PrefixSetupStepStatus.NotRun)))
            {
                results.Add(skipped);
                if (stepFinished is not null) await stepFinished(skipped);
            }

            break;
        }

        return new PrefixSetupResult(plan, Written: true, results);
    }

    private ResolvedPrefix ResolvePrefix(GameInstallation installation)
    {
        var resolution = _resolver.Resolve(installation);
        if (resolution.IsNative)
            throw new PrefixSetupRefusedException($"This game has no Wine prefix to set up. {resolution.Diagnostic}");
        if (resolution.Prefix is not { } prefix)
            throw new PrefixSetupRefusedException(resolution.Diagnostic ?? "The game's prefix could not be determined.");

        if (!ProtonPrefix.IsReady(prefix.Path))
            throw new PrefixSetupRefusedException($"The prefix at {prefix.Path} has not been created yet. Start the game once so the launcher creates it, then run the setup. SilverModManager never creates a prefix itself.");

        if (!_tools.CanRun(installation, out var diagnostic))
            throw new PrefixSetupRefusedException($"Programs cannot be run in the prefix at {prefix.Path}, so it cannot be set up from here: {diagnostic}");

        return prefix;
    }

    private IReadOnlyList<PrefixSetupStep> BuildSteps(ResolvedPrefix prefix, IReadOnlyList<PrefixComponentId> ids)
    {
        var components = ids.Select(_component).DistinctBy(c => c.Id).ToList();
        return components.Select(component => new PrefixSetupStep(
            component.Id,
            component.DisplayName,
            () => _inspector.Inspect(prefix, [component.Id])[0],
            new PrefixSetupStepPlan(component.Downloads, component.InstallSteps, component.Environment.ToImmutableDictionary()))).ToList();
    }

    private PrefixSetupPlan MakePlan(ResolvedPrefix prefix, IReadOnlyList<PrefixSetupStep> steps)
    {
        var entries = steps.Select(step =>
        {
            var detected = step.Detect();
            return new PrefixSetupPlanEntry(step.Id, step.Name, detected, step.Plan.Downloads, step.Plan.Actions.Select(Describe).ToList(), WillInstall: detected.State != PrefixComponentState.Installed);
        }).ToList();

        var touchesRegistry = steps.Where((_, i) => entries[i].WillInstall).Any(s => s.Plan.TouchesRegistry);
        var backup = touchesRegistry ? BackupDirectoryFor(prefix) : (AbsolutePath?)null;
        var shared = prefix.Kind == CompatibilityLayerKind.SteamProton
            ? "This is the game's Steam prefix. Every Proton use of the game shares it, so anything installed here also changes Steam launches and other tools that run the game."
            : null;

        return new PrefixSetupPlan(prefix, entries, backup, shared);
    }

    private static string Describe(PrefixInstallStep action) => action.Kind switch
    {
        PrefixInstallStepKind.RunInstaller => $"run {action.FileName} {string.Join(' ', action.Arguments)}".TrimEnd(),
        _ => $"copy {action.FileName} to drive_c/windows/{SystemFolder(action.Architecture)}/{action.DestinationName ?? action.FileName}",
    };

    private static string SystemFolder(PrefixArchitecture architecture) => architecture == PrefixArchitecture.X64 ? "system32" : "syswow64";

    private AbsolutePath AppData => _fileSystem.GetKnownPath(KnownPath.XDG_DATA_HOME).Combine(ApplicationConstants.DataDirectoryName);

    private AbsolutePath DownloadCache => _fileSystem.GetKnownPath(KnownPath.XDG_CACHE_HOME).Combine(ApplicationConstants.DataDirectoryName).Combine(CacheFolder);

    private AbsolutePath BackupDirectoryFor(ResolvedPrefix prefix)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prefix.Path.ToString()))).ToLowerInvariant()[..16];
        var stamp = _time.GetUtcNow().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        return AppData.Combine(BackupFolder).Combine(key).Combine(stamp);
    }

    private void RequireConsent(ResolvedPrefix prefix, string? confirmed)
    {
        var actual = prefix.Path.ToString();
        var steam = IsInSteamLibrary(prefix.Path)
            ? " It sits in a Steam library, so the confirmation has to name it exactly."
            : "";

        if (string.IsNullOrWhiteSpace(confirmed))
            throw new PrefixSetupRefusedException($"Changing a prefix needs a confirmation that names it. Confirm {actual} to continue.{steam}");

        if (!string.Equals(confirmed.TrimEnd('/'), actual.TrimEnd('/'), StringComparison.Ordinal))
            throw new PrefixSetupRefusedException($"The confirmation names {confirmed}, but this game's prefix is {actual}. Nothing was changed.{steam}");
    }

    private bool IsInSteamLibrary(AbsolutePath path)
    {
        var text = path.ToString();
        var roots = new[] { _fileSystem.GetKnownPath(KnownPath.HomeDirectory).Combine(".local/share/Steam").ToString(), "/mnt/steam" };
        return roots.Any(root => text == root || text.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal));
    }

    private void RequireNoRunningProcesses(GameInstallation installation, ResolvedPrefix prefix)
    {
        var running = _processes.FindGameProcesses(installation)
            .Concat(_processes.FindByPrefix(prefix.Path))
            .DistinctBy(p => p.Pid)
            .ToList();
        if (running.Count == 0) return;

        var names = string.Join(", ", running.Select(p => $"{p.Name} (pid {p.Pid})"));
        throw new PrefixSetupRefusedException($"The game or its Wine session is still running: {names}. Close the game and let Wine exit, then try again.");
    }

    private void BackUpRegistry(ResolvedPrefix prefix, AbsolutePath backup)
    {
        backup.CreateDirectory();
        foreach (var name in new[] { "system.reg", "user.reg" })
        {
            var source = prefix.Path.Combine(name);
            if (!source.FileExists) continue;
            using var from = source.Read();
            using var to = backup.Combine(name).Create();
            from.CopyTo(to);
        }
    }

    private async Task<PrefixSetupStepResult> RunStep(
        GameInstallation installation,
        ResolvedPrefix prefix,
        PrefixSetupStep step,
        PrefixSetupPlanEntry entry,
        int attempts,
        CancellationToken cancellationToken)
    {
        if (!entry.WillInstall) return new PrefixSetupStepResult(step.Id, step.Name, PrefixSetupStepStatus.AlreadyInstalled);

        string? error = null;
        for (var attempt = 1; attempt <= Math.Max(1, attempts); attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                error = await Install(installation, prefix, step, cancellationToken);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                error = e.Message;
            }

            if (error is null) return new PrefixSetupStepResult(step.Id, step.Name, PrefixSetupStepStatus.Installed);
        }

        return new PrefixSetupStepResult(step.Id, step.Name, PrefixSetupStepStatus.Failed, error);
    }

    /// <summary>
    /// One attempt at a step. Returns the reason it failed, or null when the prefix now shows the component.
    /// </summary>
    private async Task<string?> Install(GameInstallation installation, ResolvedPrefix prefix, PrefixSetupStep step, CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, AbsolutePath>();
        foreach (var download in step.Plan.Downloads)
        {
            if (await Fetch(download, cancellationToken) is { } mismatch) return mismatch;
            files[download.FileName] = CachePath(download);
        }

        foreach (var action in step.Plan.Actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var failure = action.Kind == PrefixInstallStepKind.RunInstaller
                ? await RunInstaller(installation, step, action, files[action.FileName], cancellationToken)
                : await CopyDll(prefix, action, files[action.FileName]);
            if (failure is not null) return failure;
        }

        var detected = step.Detect();
        if (detected.State != PrefixComponentState.Installed)
            return $"The install of {step.Name} finished, but the prefix does not show it: {detected.Evidence}.";

        await AppendToWinetricksLog(prefix, step.Id);
        return null;
    }

    private AbsolutePath CachePath(PrefixDownload download) => DownloadCache.Combine(download.Sha256[..16]).Combine(download.FileName);

    /// <summary>
    /// Makes sure the verified file is in the cache. Returns a message when the download does not have the
    /// pinned hash, and never leaves such a file behind.
    /// </summary>
    private async Task<string?> Fetch(PrefixDownload download, CancellationToken cancellationToken)
    {
        var path = CachePath(download);
        if (path.FileExists && await Sha256Of(path, cancellationToken) == download.Sha256) return null;

        var partial = path.Parent.Combine(path.FileName + ".part");
        try
        {
            await _fetcher.Fetch(download.Url, partial, cancellationToken);
            var actual = await Sha256Of(partial, cancellationToken);
            if (actual != download.Sha256)
                return $"{download.FileName} from {download.Url} has SHA-256 {actual}, not the pinned {download.Sha256}. It was not used.";

            _fileSystem.MoveFile(partial, path, overwrite: true);
            return null;
        }
        finally
        {
            if (partial.FileExists) partial.Delete();
        }
    }

    private static async Task<string> Sha256Of(AbsolutePath path, CancellationToken cancellationToken)
    {
        await using var stream = path.Read();
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private async Task<string?> RunInstaller(GameInstallation installation, PrefixSetupStep step, PrefixInstallStep action, AbsolutePath file, CancellationToken cancellationToken)
    {
        var command = PrefixCommand.ForExe(file, action.Arguments, step.Plan.Environment);
        var result = await _tools.RunInPrefix(installation, command, cancellationToken);
        if (InstallerSuccessCodes.Contains(result.ExitCode)) return null;

        var output = (result.Stderr.Length > 0 ? result.Stderr : result.Stdout).Trim();
        return $"{action.FileName} failed with exit code {result.ExitCode}." + (output.Length > 0 ? $" Its output ended: {output[^Math.Min(output.Length, 300)..]}" : "");
    }

    private static async Task<string?> CopyDll(ResolvedPrefix prefix, PrefixInstallStep action, AbsolutePath source)
    {
        var folder = prefix.Path.Combine("drive_c/windows").Combine(SystemFolder(action.Architecture));
        if (!folder.DirectoryExists())
            return $"The prefix has no {SystemFolder(action.Architecture)} folder at {folder}, so {action.FileName} cannot be placed. A 32-bit-only prefix is not supported.";

        await folder.Combine(action.DestinationName ?? action.FileName).WriteAllBytesAsync(await source.ReadAllBytesAsync());
        return null;
    }

    private static async Task AppendToWinetricksLog(ResolvedPrefix prefix, PrefixComponentId id)
    {
        var log = WineParser.GetWinetricksLogFilePath(prefix.Path);
        if (WineParser.ParseWinetricksLogFile(log).Contains(id.Value)) return;

        var existing = log.FileExists ? await log.ReadAllTextAsync() : "";
        var separator = existing.Length == 0 || existing.EndsWith('\n') ? "" : "\n";
        await log.WriteAllTextAsync(existing + separator + id.Value + "\n");
    }
}
