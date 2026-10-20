using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using NexusMods.App.UI.Extensions;
using NexusMods.App.UI.Overlays;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk.Games;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NexusMods.App.UI.Pages.SteamLaunchOption;

public class SteamLaunchOptionViewModel : AOverlayViewModel<ISteamLaunchOptionViewModel, Unit>, ISteamLaunchOptionViewModel
{
    private const string CloseSteamText = "Close Steam completely, including its tray icon, to write the launch option. " +
                                          "Steam rewrites its config when it exits and would discard the change.";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly IProcessTracker _processTracker;
    private readonly ISteamLaunchOptionWriter _writer;
    private readonly IAvaloniaInterop _interop;

    public SteamLaunchOptionViewModel(string loadoutName, GameInstallation installation, IProcessTracker processTracker, ISteamLaunchOptionWriter writer, IAvaloniaInterop interop)
    {
        LoadoutName = loadoutName;
        Installation = installation;
        _processTracker = processTracker;
        _writer = writer;
        _interop = interop;

        Line = writer.ComposeLine(loadoutName);
        Warning = writer.MissingShimWarning ?? "";
        ApplySteamState(processTracker.IsSteamRunning());

        CopyCommand = ReactiveCommand.CreateFromTask(CopyAsync);
        WriteCommand = ReactiveCommand.CreateFromTask(WriteAsync, this.WhenAnyValue(vm => vm.CanWrite));
        CloseCommand = ReactiveCommand.Create(() => Complete(Unit.Default));

        // The user may close Steam with the dialog open, so keep the Write button in step with the process table.
        this.WhenActivated(d =>
        {
            Observable.Interval(PollInterval, TaskPoolScheduler.Default)
                .Select(_ => _processTracker.IsSteamRunning())
                .DistinctUntilChanged()
                .OnUI()
                .Subscribe(ApplySteamState)
                .DisposeWith(d);
        });
    }

    public string LoadoutName { get; }
    public GameInstallation Installation { get; }
    public string Line { get; }
    public string Warning { get; }
    public string CopyNote => "Copying changes nothing in Steam. Paste the line into the game's Properties > Launch Options yourself.";
    [Reactive] public bool IsSteamRunning { get; private set; }
    [Reactive] public bool CanWrite { get; private set; }
    [Reactive] public string WriteNote { get; private set; } = "";
    [Reactive] public string Message { get; private set; } = "";
    public ReactiveCommand<Unit, Unit> CopyCommand { get; }
    public ReactiveCommand<Unit, Unit> WriteCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    public void RefreshSteamState() => ApplySteamState(_processTracker.IsSteamRunning());

    public async Task CopyAsync()
    {
        try
        {
            await _interop.SetClipboardTextAsync(Line);
            Message = "Copied. Nothing was changed in Steam.";
        }
        catch (InvalidOperationException ex)
        {
            Message = $"Could not copy to the clipboard: {ex.Message}";
        }
    }

    public Task WriteAsync()
    {
        // The button state may be a poll behind, so look again before touching Steam's files.
        RefreshSteamState();
        if (IsSteamRunning)
        {
            Message = CloseSteamText;
            return Task.CompletedTask;
        }

        return WriteToSteam();
    }

    private async Task WriteToSteam()
    {
        var result = await Task.Run(() => _writer.Write(Installation, LoadoutName));
        Message = result.IsRefused
            ? result.Refusal!
            : $"Set the launch options of {Installation.Game.DisplayName} to: {result.Line} " +
              $"The previous file was kept as {result.Backup}. The line runs the current deploy of the loadout.";
    }

    private void ApplySteamState(bool running)
    {
        IsSteamRunning = running;
        CanWrite = !running;
        WriteNote = running ? CloseSteamText : "";
    }
}
