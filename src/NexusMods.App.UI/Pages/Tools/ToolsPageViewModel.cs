using Avalonia.Platform.Storage;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Sdk.Loadouts;
using NexusMods.App.UI.Windows;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Jobs;
using NexusMods.UI.Sdk.Icons;
using R3;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Unit = R3.Unit;

namespace NexusMods.App.UI.Pages.Tools;

[UsedImplicitly]
internal class ToolsPageViewModel : APageViewModel<IToolsPageViewModel>, IToolsPageViewModel
{
    private readonly ILogger<ToolsPageViewModel> _logger;
    private readonly IConnection _conn;
    private readonly IToolManager _toolManager;
    private readonly IToolRunner _toolRunner;
    private readonly IPrefixResolver _prefixResolver;
    private readonly IAvaloniaInterop _avaloniaInterop;
    private readonly IJobMonitor _monitor;
    private readonly BehaviorSubject<bool> _canRunWindowsProgram = new(false);

    [Reactive] public LoadoutId LoadoutId { get; set; }
    [Reactive] public IReadOnlyList<ITool> Tools { get; private set; } = [];
    [Reactive] public IReadOnlyList<UserTool.ReadOnly> UserTools { get; private set; } = [];
    [Reactive] public bool CanRunWindowsProgram { get; private set; }
    [Reactive] public string? Diagnostic { get; private set; }
    [Reactive] public string? PrefixPath { get; private set; }
    [Reactive] public string? Strategy { get; private set; }
    [Reactive] public string? LastRunMessage { get; private set; }

    public R3.ReactiveCommand<ITool> RunToolCommand { get; }
    public R3.ReactiveCommand<Unit> RunWindowsProgramCommand { get; }

    public ToolsPageViewModel(
        IWindowManager windowManager,
        ILogger<ToolsPageViewModel> logger,
        IConnection conn,
        IToolManager toolManager,
        IToolRunner toolRunner,
        IPrefixResolver prefixResolver,
        IAvaloniaInterop avaloniaInterop,
        IJobMonitor monitor) : base(windowManager)
    {
        _logger = logger;
        _conn = conn;
        _toolManager = toolManager;
        _toolRunner = toolRunner;
        _prefixResolver = prefixResolver;
        _avaloniaInterop = avaloniaInterop;
        _monitor = monitor;

        TabIcon = IconValues.Wrench;
        TabTitle = "Tools";

        RunToolCommand = new R3.ReactiveCommand<ITool>(executeAsync: async (tool, token) => await ReportFailure(() => RunToolAsync(tool, token)));
        RunWindowsProgramCommand = _canRunWindowsProgram.ToReactiveCommand<Unit>(
            async (_, token) => await ReportFailure(() => RunWindowsProgramAsync(token)),
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
            ApplyRunnability(canRun: false, diagnostic: null, prefixPath: null, strategy: null);
            return;
        }

        var loadout = LoadLoadout();
        var installation = loadout.InstallationInstance;
        Tools = _toolManager.GetTools(loadout).ToArray();
        UserTools = UserTool.FindByInstallation(_conn.Db, loadout.InstallationId).OrderBy(t => t.Name).ToArray();

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
    // shown on the page instead.
    private async ValueTask ReportFailure(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Running a tool from the Tools page failed");
            LastRunMessage = ex.Message;
        }
    }

    internal async Task RunToolAsync(ITool tool, CancellationToken token)
    {
        LastRunMessage = null;
        var loadout = LoadLoadout();
        await Task.Run(async () => await _toolManager.RunTool(tool, loadout, _monitor, token), token);
        LastRunMessage = $"{tool.Name} finished.";
    }

    internal async Task RunWindowsProgramAsync(CancellationToken token)
    {
        var loadout = LoadLoadout();
        if (!_toolRunner.CanRun(loadout.InstallationInstance, out var diagnostic))
            throw new InvalidOperationException($"Cannot run a Windows program for {loadout.InstallationInstance.Game.DisplayName}: {diagnostic}");

        var picked = await _avaloniaInterop.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a Windows program to run in the game's prefix",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Windows programs") { Patterns = ["*.exe"] }],
        });
        if (picked.Length == 0) return;

        await RunToolAsync(new WindowsProgramTool(_toolRunner, loadout.Game.GameId, picked[0]), token);
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
