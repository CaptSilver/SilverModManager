using System.Diagnostics;
using SilverModManager.Sdk.Errors;
using Avalonia.Platform.Storage;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Pages.Diagnostics.PrefixSetup;
using SilverModManager.Platform.Proton.Tools;
using SilverModManager.Platform.Proton.UserTools;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;
using SilverModManager.UI.Sdk.Icons;
using R3;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Unit = R3.Unit;

namespace SilverModManager.App.UI.Pages.Tools;

[UsedImplicitly]
internal partial class ToolsPageViewModel : APageViewModel<IToolsPageViewModel>, IToolsPageViewModel
{
    private readonly ILogger<ToolsPageViewModel> _logger;
    private readonly IConnection _conn;
    private readonly IToolManager _toolManager;
    private readonly IToolRunner _toolRunner;
    private readonly ModdingToolFactory _toolFactory;
    private readonly IPrefixResolver _prefixResolver;
    private readonly IAvaloniaInterop _avaloniaInterop;
    private readonly IJobMonitor _monitor;
    private readonly ToolInstaller _installer;
    private readonly ToolPipelineRunner _pipelineRunner;
    private readonly IPrefixSetupLauncher _prefixSetup;
    private readonly object _runLock = new();
    private CancellationTokenSource? _runCancellation;
    private string? _runningName;
    private readonly BehaviorSubject<bool> _canRunWindowsProgram = new(false);

    [Reactive] public partial LoadoutId LoadoutId { get; set; }
    [Reactive] public partial IReadOnlyList<ITool> Tools { get; private set; } = [];
    [Reactive] public partial IReadOnlyList<UserTool.ReadOnly> UserTools { get; private set; } = [];
    [Reactive] public partial IReadOnlyList<CatalogueToolRow> CatalogueTools { get; private set; } = [];
    [Reactive] public partial IReadOnlyList<string> Plugins { get; private set; } = [];
    [Reactive] public partial IReadOnlyList<ToolPipeline> Pipelines { get; private set; } = [];
    [Reactive] public partial bool IsRunning { get; private set; }
    [Reactive] public partial string? RunningMessage { get; private set; }
    [Reactive] public partial bool CanRunWindowsProgram { get; private set; }
    [Reactive] public partial string? Diagnostic { get; private set; }
    [Reactive] public partial string? PrefixPath { get; private set; }
    [Reactive] public partial string? Strategy { get; private set; }
    [Reactive] public partial string? LastRunMessage { get; private set; }

    public R3.ReactiveCommand<ITool> RunToolCommand { get; }
    public R3.ReactiveCommand<Unit> RunWindowsProgramCommand { get; }
    public R3.ReactiveCommand<QuickAutoCleanRequest> QuickAutoCleanCommand { get; }
    public R3.ReactiveCommand<ToolPipeline> RunPipelineCommand { get; }
    public R3.ReactiveCommand<bool> AddToolCommand { get; }
    public R3.ReactiveCommand<CatalogueToolRow> InstallPrerequisitesCommand { get; }
    public R3.ReactiveCommand<Unit> CancelRunCommand { get; }

    public ToolsPageViewModel(
        IWindowManager windowManager,
        ILogger<ToolsPageViewModel> logger,
        IConnection conn,
        IToolManager toolManager,
        IToolRunner toolRunner,
        ModdingToolFactory toolFactory,
        IPrefixResolver prefixResolver,
        IAvaloniaInterop avaloniaInterop,
        IJobMonitor monitor,
        ToolInstaller installer,
        ToolPipelineRunner pipelineRunner,
        IPrefixSetupLauncher prefixSetup) : base(windowManager)
    {
        _logger = logger;
        _conn = conn;
        _toolManager = toolManager;
        _toolRunner = toolRunner;
        _toolFactory = toolFactory;
        _prefixResolver = prefixResolver;
        _avaloniaInterop = avaloniaInterop;
        _monitor = monitor;
        _installer = installer;
        _pipelineRunner = pipelineRunner;
        _prefixSetup = prefixSetup;

        TabIcon = IconValues.Wrench;
        TabTitle = "Tools";

        RunToolCommand = new R3.ReactiveCommand<ITool>(executeAsync: async (tool, token) => await RunToolReportingAsync(tool, token));
        QuickAutoCleanCommand = new R3.ReactiveCommand<QuickAutoCleanRequest>(executeAsync: async (request, token) =>
            await ReportFailure(request.Tool.Name, () => RunQuickAutoCleanAsync(request.Tool, request.Plugin, token)));
        RunPipelineCommand = new R3.ReactiveCommand<ToolPipeline>(executeAsync: async (pipeline, token) => await RunPipelineReportingAsync(pipeline, token));
        AddToolCommand = new R3.ReactiveCommand<bool>(executeAsync: async (folder, token) => await AddToolFromPickerAsync(folder));
        InstallPrerequisitesCommand = new R3.ReactiveCommand<CatalogueToolRow>(executeAsync: async (row, token) =>
            await ReportFailure(row.Name, () => InstallPrerequisitesAsync(row)));
        CancelRunCommand = new R3.ReactiveCommand<Unit>(_ => CancelRun());
        RunWindowsProgramCommand = _canRunWindowsProgram.ToReactiveCommand<Unit>(
            async (_, token) => await ReportFailure("The program", () => RunWindowsProgramAsync(token)),
            initialCanExecute: false
        );

        this.WhenAnyValue(vm => vm.LoadoutId).Subscribe(_ => Refresh());
    }

    private Loadout.ReadOnly LoadLoadout() => Loadout.Load(_conn.Db, LoadoutId);

    private void Refresh()
    {
        if (LoadoutId == default)
        {
            Tools = [];
            UserTools = [];
            CatalogueTools = [];
            Plugins = [];
            Pipelines = [];
            ApplyRunnability(canRun: false, diagnostic: null, prefixPath: null, strategy: null);
            return;
        }

        var loadout = LoadLoadout();
        var installation = loadout.InstallationInstance;
        Tools = _toolManager.GetTools(loadout).ToArray();
        var rows = UserTool.FindByInstallation(_conn.Db, loadout.InstallationId).OrderBy(t => t.Name).ToArray();
        UserTools = rows.Where(row => row.ToolId.HasValue is false).ToArray();
        CatalogueTools = Tools.OfType<ModdingTool>()
            .Where(tool => tool.CatalogueId is not null)
            .Select(tool => CatalogueToolRow.For(tool, installation, rows))
            .ToArray();
        Plugins = installation.Game is IToolPluginSource source ? source.PluginNames(loadout) : [];
        Pipelines = _pipelineRunner.PipelinesFor(loadout);

        var canRun = _toolRunner.CanRun(installation, out var diagnostic);
        var prefix = _prefixResolver.Resolve(installation);
        var prefixPath = prefix.IsResolved ? prefix.Prefix.Path.ToString() : null;

        // The plan only needs a program to name; the strategy does not depend on which one.
        var strategy = canRun
            ? _toolRunner.Plan(installation, installation.Locations[LocationId.Game].Path.Combine("program.exe"), [], null).Strategy
            : null;

        ApplyRunnability(canRun, diagnostic, prefixPath, strategy);
    }

    private void ApplyRunnability(bool canRun, string? diagnostic, string? prefixPath, string? strategy)
    {
        CanRunWindowsProgram = canRun;
        Diagnostic = diagnostic;
        PrefixPath = prefixPath;
        Strategy = strategy;
        _canRunWindowsProgram.OnNext(canRun);
    }

    // Commands run on a worker and a thrown exception would end up unobserved, so it is logged and
    // shown on the page instead. A refusal is only shown here; a crash also reaches the error dialog through
    // the log. A message already on the page (the steps a pipeline finished) stays above the failure.
    private async ValueTask ReportFailure(string what, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            LastRunMessage = $"{what} was cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogFailure(ex, "Running a tool from the Tools page failed");
            LastRunMessage = string.IsNullOrEmpty(LastRunMessage) ? ex.Message : $"{LastRunMessage}\n{ex.Message}";
        }
    }

    internal ValueTask RunToolReportingAsync(ITool tool, CancellationToken token)
        => ReportFailure(tool.Name, () => RunToolAsync(tool, token));

    internal ValueTask RunPipelineReportingAsync(ToolPipeline pipeline, CancellationToken token)
        => ReportFailure(pipeline.Name, () => RunPipelineAsync(pipeline, token));

    /// <summary>Stops the run in progress, if there is one.</summary>
    public void CancelRun()
    {
        lock (_runLock) _runCancellation?.Cancel();
    }

    // One run at a time: the page has a single Cancel button and a single running line, and two runs would both
    // apply and ingest the same loadout, each crediting the other's output to itself.
    private void EnsureIdle()
    {
        lock (_runLock)
        {
            if (_runCancellation is not null)
                throw new ToolRefusedException($"{_runningName} is still running. Wait for it to finish, or cancel it, before starting another.");
        }
    }

    // Holds the run's cancellation source for the Cancel button and keeps the "running for n min" line fresh.
    private async Task Tracked(string name, Func<CancellationToken, Task> run, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        lock (_runLock)
        {
            EnsureIdle();
            _runCancellation = cancellation;
            _runningName = name;
        }

        var started = Stopwatch.StartNew();
        IsRunning = true;
        RunningMessage = ToolRunMessage.Running(name, started.Elapsed);
        using var ticker = new PeriodicTimer(TimeSpan.FromSeconds(15));
        var ticking = Task.Run(async () =>
        {
            while (await ticker.WaitForNextTickAsync(cancellation.Token).ConfigureAwait(false))
                RunningMessage = ToolRunMessage.Running(name, started.Elapsed);
        }, CancellationToken.None);

        try
        {
            await run(cancellation.Token);
        }
        finally
        {
            ticker.Dispose();
            try { await ticking; } catch (OperationCanceledException) { }
            lock (_runLock)
            {
                _runCancellation = null;
                _runningName = null;
            }
            IsRunning = false;
            RunningMessage = null;
        }
    }

    internal async Task RunToolAsync(ITool tool, CancellationToken token)
    {
        EnsureIdle();
        LastRunMessage = null;
        var loadout = LoadLoadout();
        await Tracked(tool.Name, async runToken =>
        {
            var outcome = await Task.Run(async () => await _toolManager.RunToolWithOutcome(tool, loadout, _monitor, runToken), runToken);
            LastRunMessage = ToolRunMessage.Finished(tool.Name, outcome.ExitCode, outcome.OutputGroup, outcome.CapturedFiles);
        }, token);
    }

    internal Task RunQuickAutoCleanAsync(CatalogueToolRow row, string plugin, CancellationToken token)
        => RunQuickAutoCleanAsync(row.Tool, plugin, token);

    private Task RunQuickAutoCleanAsync(ModdingTool tool, string plugin, CancellationToken token)
        => RunToolAsync(tool.ForQuickAutoClean(plugin) ?? throw new ToolRefusedException($"{tool.Name} cannot clean a plugin."), token);

    internal async Task RunPipelineAsync(ToolPipeline pipeline, CancellationToken token)
    {
        EnsureIdle();
        LastRunMessage = null;
        var loadout = LoadLoadout();
        var lines = new List<string>();
        await Tracked(pipeline.Name, async runToken =>
        {
            try
            {
                await Task.Run(async () => await _pipelineRunner.Run(pipeline, loadout, _monitor, lines.Add, runToken), runToken);
            }
            finally
            {
                if (lines.Count > 0) LastRunMessage = string.Join('\n', lines);
            }
        }, token);
    }

    /// <summary>Opens the prefix setup for what a tool is missing, then checks the tool again.</summary>
    internal async Task InstallPrerequisitesAsync(CatalogueToolRow row)
    {
        await _prefixSetup.ShowAsync(LoadoutId, row.MissingComponentIds);
        Refresh();
    }

    /// <summary>Asks for a folder (or a program) and adds the tools in it to this game.</summary>
    internal async Task AddToolFromPickerAsync(bool folder)
    {
        LastRunMessage = null;
        await ReportFailure("Adding the tool", async () =>
        {
            var picked = folder
                ? await _avaloniaInterop.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose the folder a modding tool is in", AllowMultiple = false })
                : await _avaloniaInterop.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose a modding tool", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("Programs") { Patterns = ["*.exe"] }] });
            if (picked.Length == 0) return;

            var result = await _installer.Add(LoadLoadout(), picked[0]);
            var lines = result.Added.Select(name => $"Added {name}.").Concat(result.AlreadyAdded.Select(name => $"{name} is already added."));
            LastRunMessage = string.Join('\n', lines);
            Refresh();
        });
    }

    internal async Task RunWindowsProgramAsync(CancellationToken token)
    {
        EnsureIdle();
        var loadout = LoadLoadout();
        if (!_toolRunner.CanRun(loadout.InstallationInstance, out var diagnostic))
            throw new WindowsProgramRefusedException($"Cannot run a Windows program for {loadout.InstallationInstance.Game.DisplayName}: {diagnostic}");

        var picked = await _avaloniaInterop.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a Windows program to run in the game's prefix",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Windows programs") { Patterns = ["*.exe"] }],
        });
        if (picked.Length == 0) return;

        var gameId = loadout.Game.GameId;
        var definition = ToolDefinition.AdHoc(gameId, picked[0].FileName, picked[0].FileName);
        await RunToolAsync(_toolFactory.Create(definition, new InstalledTool(picked[0]), gameId), token);
    }

    public async Task AddUserTool(UserToolDraft draft)
    {
        Validate(draft);
        var loadout = LoadLoadout();

        using var tx = _conn.BeginTransaction();
        _ = new UserTool.New(tx)
        {
            InstallationId = loadout.InstallationId,
            Name = draft.Name.Trim(),
            Exe = draft.Exe,
            Arguments = draft.Arguments,
            RunVia = draft.RunVia,
        };
        await tx.Commit();
        Refresh();
    }

    public async Task UpdateUserTool(UserTool.ReadOnly tool, UserToolDraft draft)
    {
        Validate(draft);
        // Writing the fields onto a removed id would bring it back without an installation, listed nowhere.
        if (!UserTool.Load(_conn.Db, tool.Id).IsValid()) throw new ArgumentException($"'{tool.Name}' was removed.", nameof(tool));

        using var tx = _conn.BeginTransaction();
        tx.Add(tool.Id, UserTool.Name, draft.Name.Trim());
        tx.Add(tool.Id, UserTool.Exe, draft.Exe);
        tx.Add(tool.Id, UserTool.Arguments, draft.Arguments);
        tx.Add(tool.Id, UserTool.RunVia, draft.RunVia);
        await tx.Commit();
        Refresh();
    }

    public async Task RemoveUserTool(UserTool.ReadOnly tool)
    {
        using var tx = _conn.BeginTransaction();
        tx.Delete(tool.Id, recursive: false);
        await tx.Commit();
        Refresh();
    }

    private static void Validate(UserToolDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Name)) throw new ArgumentException("A tool needs a name.", nameof(draft));
        if (!System.IO.Path.IsPathRooted(draft.Exe)) throw new ArgumentException($"'{draft.Exe}' is not an absolute path to a program.", nameof(draft));
    }
}

/// <summary>
/// The game's Wine prefix can't run a Windows program, for a reason the message states.
/// </summary>
internal sealed class WindowsProgramRefusedException(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "The program was not started";
}
