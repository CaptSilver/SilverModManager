using System.Text;
using System.Text.RegularExpressions;
using CliWrap;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.Starters;
using SilverModManager.Platform.Proton.ToolRunner;
using SilverModManager.Platform.Proton.ToolRunner.Dependencies;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Launch.Starters;

/// <summary>
/// Starts a game through Heroic's <c>heroic://launch</c> URL, with Heroic run as a child (<c>--no-gui</c>) when
/// the app can find it. The child's exit is a hint, not the end: a Heroic that is already running takes the URL
/// and the new process leaves at once, so the wait is for the game's processes to show up and then be gone.
/// </summary>
public sealed partial class HeroicGameStarter : IGameStarter
{
    // The query form of the URL, which carries arguments, arrived in 2.16. Before 2.19 Heroic does not decode them.
    private static readonly Version FirstWithArguments = new(2, 16);
    private static readonly Version FirstDecodingArguments = new(2, 19);

    private readonly IExecutableLocator _executables;
    private readonly HeroicFlatpakDependency _flatpak;
    private readonly IHeroicVersionProbe _versions;
    private readonly IProcessRunner _runner;
    private readonly IRunningGameDetector _detector;
    private readonly IOSInterop _os;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<HeroicGameStarter> _logger;
    private readonly Func<string?> _xdgStateHome;

    /// <summary/>
    public HeroicGameStarter(
        IExecutableLocator executables, HeroicFlatpakDependency flatpak, IHeroicVersionProbe versions, IProcessRunner runner,
        IRunningGameDetector detector, IOSInterop os, IFileSystem fileSystem, ILogger<HeroicGameStarter> logger,
        Func<string?>? xdgStateHome = null)
    {
        _executables = executables;
        _flatpak = flatpak;
        _versions = versions;
        _runner = runner;
        _detector = detector;
        _os = os;
        _fileSystem = fileSystem;
        _logger = logger;
        _xdgStateHome = xdgStateHome ?? (() => Environment.GetEnvironmentVariable("XDG_STATE_HOME"));
    }

    /// <inheritdoc />
    public GameStarterKind Kind => GameStarterKind.Heroic;

    /// <inheritdoc />
    public bool CanCarryArguments(GameStartRequest request)
    {
        var client = ClientLaunchWait.ClientOf(request, StoreClientKind.Heroic);
        if (request.AllArgs.Length == 0) return true;
        return LaunchUrl(client.AppName, HeroicLaunchLog.RunnerOf(request.Installation.LocatorResult.Store), request.AllArgs, _versions.Read(client.IsFlatpak)) is not null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Never refuses: with neither a binary nor the Flatpak the URL goes to whatever handles <c>heroic://</c> on the
    /// desktop, which the app cannot ask about. A Heroic that is not there shows up as a start timeout.
    /// </remarks>
    public void CheckCanStart(GameStartRequest request) => ClientLaunchWait.ClientOf(request, StoreClientKind.Heroic);

    /// <summary>
    /// The URL that starts <paramref name="appName"/> with <paramref name="args"/> on a Heroic of this version
    /// (null when unknown), or null when that Heroic cannot carry them. Below 2.16 only the path form exists, and
    /// it has no arguments. From 2.16 the query form repeats <c>arg</c>, and Heroic reads the values as they are
    /// until 2.19 decodes them, so on those versions a value that needs encoding cannot be sent.
    /// </summary>
    internal static Uri? LaunchUrl(string appName, string runner, string[] args, Version? heroic)
    {
        if (heroic is not null && heroic < FirstWithArguments)
            return args.Length == 0 ? new Uri($"heroic://launch/{Uri.EscapeDataString(runner)}/{Uri.EscapeDataString(appName)}") : null;

        var encode = heroic is null || heroic >= FirstDecodingArguments;
        if (!encode && args.Any(arg => !UrlSafe().IsMatch(arg))) return null;

        var query = new StringBuilder($"appName={Uri.EscapeDataString(appName)}&runner={Uri.EscapeDataString(runner)}");
        foreach (var arg in args) query.Append("&arg=").Append(encode ? Uri.EscapeDataString(arg) : arg);
        query.Append("&gui=false");
        return new Uri($"heroic://launch?{query}");
    }

    [GeneratedRegex(@"^[A-Za-z0-9._~-]+$")]
    private static partial Regex UrlSafe();

    /// <inheritdoc />
    public async Task<GameStartOutcome> Start(GameStartRequest request, IGameLaunch launch, CancellationToken token)
    {
        var client = ClientLaunchWait.ClientOf(request, StoreClientKind.Heroic);
        var runnerName = HeroicLaunchLog.RunnerOf(request.Installation.LocatorResult.Store);
        var version = _versions.Read(client.IsFlatpak);
        var url = LaunchUrl(client.AppName, runnerName, request.AllArgs, version)
            ?? throw new GameLaunchException($"This Heroic cannot pass {string.Join(' ', request.AllArgs)} to {request.GameName}. Update Heroic, or start the game another way.");

        var log = HeroicLaunchLog.PathFor(_fileSystem, client, runnerName, _xdgStateHome());
        var logSeen = LastWrite(log);
        // Read before the launch so what Heroic writes first counts.
        bool LogWritten()
        {
            var now = LastWrite(log);
            var moved = now != logSeen;
            logSeen = now;
            return moved;
        }

        var child = StartHeroic(client, url, request, token);
        bool ClientIsPreparing()
        {
            var written = LogWritten();
            return written || child is { IsCompleted: false };
        }

        var outcome = await ClientLaunchWait.UntilStartedThenGone(request, launch, _detector, ClientIsPreparing, _logger, "Heroic", token);
        if (outcome == GameStartOutcome.TimedOut && request.AllArgs.Length > 0 && version is null)
            _logger.LogWarning("Heroic's version could not be read. Passing arguments needs Heroic {FirstWithArguments} or newer, and {FirstDecoding} for values that need encoding", FirstWithArguments, FirstDecodingArguments);
        return outcome;
    }

    private static DateTime? LastWrite(AbsolutePath log) => log.FileExists ? log.FileInfo.LastWriteTimeUtc : null;

    /// <summary>Runs Heroic as a child, or hands the URL to the desktop when there is no Heroic to run. Null for the latter.</summary>
    private Task<CommandResult>? StartHeroic(StoreClient client, Uri url, GameStartRequest request, CancellationToken token)
    {
        var native = _executables.Find("heroic");
        var binary = ClientLaunchWait.Choose(client.IsFlatpak, native is not null, _flatpak.IsInstalled);
        // The string as built: AbsoluteUri would add a slash after the host, which is not the form Heroic documents.
        var arguments = new[] { "--no-gui", url.OriginalString };
        Command command;
        if (binary == ClientBinary.Flatpak)
            command = Cli.Wrap("flatpak").WithArguments(["run", HeroicFlatpakDependency.FlatpakId, ..arguments]);
        else if (binary == ClientBinary.Native && native is { } heroic)
            command = Cli.Wrap(heroic.ToString()).WithArguments(arguments);
        else
        {
            _logger.LogInformation("No Heroic binary or Flatpak found, so {Url} goes to the desktop's handler", url.OriginalString);
            _os.OpenUri(url);
            return null;
        }

        var stderr = new OutputTail();
        command = command.WithValidation(CommandResultValidation.None).WithStandardErrorPipe(stderr);
        _logger.LogInformation("Starting {Game} through Heroic: `{Command}`", request.GameName, command.ToString());
        var child = _runner.RunAsync(command, logOutput: true, token);
        _ = ReportEarlyFailure(child, stderr, request);
        return child;
    }

    // A child that fails before the game shows up is the only trace of why a launch went nowhere.
    private async Task ReportEarlyFailure(Task<CommandResult> child, OutputTail stderr, GameStartRequest request)
    {
        try
        {
            var result = await child;
            if (result.ExitCode != 0 && !_detector.Running(request.Installation).Any)
                _logger.LogWarning("Heroic exited with code {ExitCode} before {Game} started: {Stderr}", result.ExitCode, request.GameName, stderr.Text);
        }
        catch (OperationCanceledException)
        {
            // The launch was stopped; the session says so.
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Heroic could not be run to start {Game}", request.GameName);
        }
    }

    /// <inheritdoc />
    public string? AfterSessionMessage(GameStartRequest request, GameStartOutcome outcome)
        => outcome == GameStartOutcome.TimedOut
            ? $"Heroic did not start {request.GameName} within {SteamGameStarter.DescribeTimeout(request.Timing.StartTimeout)}. Make sure Heroic is installed and signed in, then press Play again."
            : null;
}
