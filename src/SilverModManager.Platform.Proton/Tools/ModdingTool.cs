using CliWrap;
using NexusMods.Paths;
using R3;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.DataModel;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Platform.Proton.Tools;

/// <summary>
/// Runs one program from a <see cref="ToolDefinition"/>, as the user has it installed. The loadout is
/// synchronized around the run by <see cref="IToolManager.RunTool"/>, so what the program writes into the game
/// folder is ingested like for any other tool. Windows programs go through <see cref="IToolRunner"/>, which
/// turns host paths into Wine ones; native programs are started directly.
/// </summary>
public sealed class ModdingTool(
    ToolDefinition definition,
    InstalledTool installed,
    GameId gameId,
    IToolRunner toolRunner,
    IProcessRunner processRunner,
    ToolArgumentResolver resolver,
    IPrefixResolver prefixResolver,
    ToolOutputCapture outputCapture,
    ToolPrefixSetup prefixSetup,
    IReadOnlyDictionary<string, string>? variables = null) : ITool, IToolWithOutput, IToolWithRunSummary
{
    public AbsolutePath Exe => installed.Exe;

    /// <summary>The catalogue id of the definition, or null for a program the user added by hand.</summary>
    public ToolId? CatalogueId => definition.Id;

    public ToolPrefixRequirements Prefix => definition.Prefix;

    /// <inheritdoc/>
    public ToolRunSummary? LastRun { get; private set; }

    public bool CanQuickAutoClean => definition.QuickAutoClean is not null;

    /// <summary>
    /// The same program set up to clean <paramref name="plugin"/> (a file name) and exit, or null when the
    /// definition has no such mode.
    /// </summary>
    public ModdingTool? ForQuickAutoClean(string plugin)
        => definition.QuickAutoClean is not { } variant
            ? null
            : new ModdingTool(variant.Apply(definition), installed, gameId, toolRunner, processRunner, resolver, prefixResolver, outputCapture, prefixSetup,
                new Dictionary<string, string>(variables ?? new Dictionary<string, string>()) { [ToolVariableNames.Plugin] = plugin });

    /// <summary>
    /// The display names of the prefix components the tool needs and the installation's prefix lacks. Empty when
    /// the tool needs none, or the prefix cannot be inspected.
    /// </summary>
    public IReadOnlyList<string> MissingPrerequisites(GameInstallation installation)
        => prefixSetup.MissingComponents(installation, definition.Prefix);

    public IEnumerable<GameId> GameIds => [gameId];

    public string Name => definition.DisplayName;

    public ToolOutput Output => WithGroupName(definition.Output);

    public bool DisableOwnOutputDuringRun => definition.DisableOwnOutputDuringRun;

    public IReadOnlyList<string> IgnoredOutput => definition.IgnoredOutput;

    public IJobTask<ITool, Unit> StartJob(Loadout.ReadOnly loadout, IJobMonitor monitor, CancellationToken cancellationToken)
    {
        return monitor.Begin<ITool, Unit>(this, async _ =>
        {
            await Execute(loadout, cancellationToken);
            return Unit.Default;
        });
    }

    internal async Task Execute(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        var via = installed.RunVia ?? definition.RunVia;
        AbsolutePath? runFolder = definition.Output is ToolOutput.DeclaredFolder ? CreateRunFolder(loadout.InstallationInstance, via) : null;
        var started = false;
        int exitCode;
        try
        {
            var args = Arguments(loadout, runFolder);
            exitCode = via is ToolRunVia.Native
                ? await RunNative(args, cancellationToken)
                : await RunWindows(loadout.InstallationInstance, args, via, () => started = true, cancellationToken);
            started = true;
        }
        catch (Exception exception) when (runFolder is { } folder)
        {
            // Refused before the tool started: nothing was written, so nothing is worth keeping.
            if (started is false)
            {
                folder.DeleteDirectory(recursive: true);
                throw;
            }

            if (exception is OperationCanceledException) throw;
            throw new InvalidOperationException($"{exception.Message} The tool's output was kept at {folder}.", exception);
        }

        var captured = 0;
        var kept = runFolder is null ? string.Empty : $" Its output was kept at {runFolder}.";
        if (definition.ExitCodes is ExitCodePolicy.ZeroIsSuccess && exitCode != 0)
            throw new InvalidOperationException($"{Name} exited with code {exitCode}; see the process log.{kept}");

        if (runFolder is not null && Output is ToolOutput.DeclaredFolder output)
        {
            captured = (await outputCapture.CaptureFolder(loadout, runFolder.Value, output.MapsTo, output.GroupName, definition.IgnoredOutput, cancellationToken)).FileCount;
            runFolder.Value.DeleteDirectory(recursive: true);
        }

        LastRun = new ToolRunSummary(exitCode, captured);
    }

    // The user may have renamed the group in the tools list; the definition's name is the default.
    private ToolOutput WithGroupName(ToolOutput output) => installed.OutputGroupName is not { } name ? output : output switch
    {
        ToolOutput.DeclaredFolder declared => declared with { GroupName = name },
        ToolOutput.GameFolder gameFolder => gameFolder with { GroupName = name },
        _ => output,
    };

    // The folder the tool writes to is in the prefix (see ToolRunFolder), so a tool that runs natively has none.
    // A definition without a catalogue id has no name to give the folder.
    private AbsolutePath CreateRunFolder(GameInstallation installation, ToolRunVia via)
    {
        if (definition.Id is not { } id)
            throw new InvalidOperationException($"Cannot run {Name}: it writes output to a folder but has no catalogue id to name the run folder after.");
        if (via is ToolRunVia.Native)
            throw new InvalidOperationException($"Cannot run {Name} natively: the folder it writes to is kept in the game's Wine prefix.");

        var prefix = prefixResolver.Resolve(installation);
        if (prefix.IsResolved is false)
            throw new InvalidOperationException($"Cannot run {Name} for {installation.Game.DisplayName}: {prefix.Diagnostic ?? "this installation does not run in a Wine prefix."}");

        return ToolRunFolder.Create(prefix.Prefix.Path, id, DateTimeOffset.Now);
    }

    // A line the user typed replaces the definition's words. Host paths go in as they are: the tool runner
    // translates the arguments of a Windows exe, and a second pass here would translate twice.
    private IReadOnlyList<string> Arguments(Loadout.ReadOnly loadout, AbsolutePath? runFolder)
    {
        var extra = new Dictionary<string, string>(variables ?? new Dictionary<string, string>()) { [ToolVariableNames.Exe] = Exe.GetFullPath() };
        if (runFolder is { } folder) extra[ToolVariableNames.OutputFolder] = folder.GetFullPath();
        if (installed.Arguments is { } line)
            return resolver.ResolveLine(line, loadout, extra);

        var words = resolver.Resolve(definition.Arguments, loadout, extra);
        var game = loadout.InstallationInstance.GetGame();
        return definition.ExtraArguments is { } more ? [.. words, .. more(game, Exe.FileName)] : words;
    }

    private async Task<int> RunWindows(GameInstallation installation, IReadOnlyList<string> args, ToolRunVia via, Action onStart, CancellationToken cancellationToken)
    {
        // Plan with the strategy that will run, not Auto: a forced strategy can run in a prefix Auto cannot.
        // Without a way to run, starting quietly would leave the user wondering why nothing happened.
        var plan = toolRunner.Plan(installation, Exe, args, null, via);
        if (plan.IsRunnable is false)
            throw new InvalidOperationException($"Cannot run {Name} for {installation.Game.DisplayName}: {plan.Diagnostic}");

        await prefixSetup.Ensure(installation, Name, definition.Prefix, cancellationToken);

        onStart();
        var result = await toolRunner.RunWindowsTool(installation, Exe, args, null, logOutput: true, cancellationToken, via);
        return result.ExitCode;
    }

    private async Task<int> RunNative(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var command = Cli.Wrap(Exe.ToString())
            .WithArguments(args)
            .WithWorkingDirectory(Exe.Parent.ToString())
            .WithValidation(CommandResultValidation.None);
        var result = await processRunner.RunAsync(command, logOutput: true, cancellationToken);
        return result.ExitCode;
    }
}
