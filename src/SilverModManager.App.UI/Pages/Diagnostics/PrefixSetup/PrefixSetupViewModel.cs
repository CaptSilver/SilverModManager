using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using SilverModManager.App.UI.Overlays;
using SilverModManager.App.UI.Resources;
using SilverModManager.Platform.Proton.PrefixSetup;
using SilverModManager.Sdk.Games;

namespace SilverModManager.App.UI.Pages.Diagnostics.PrefixSetup;

public sealed partial class PrefixSetupViewModel : AOverlayViewModel<IPrefixSetupViewModel, Unit>, IPrefixSetupViewModel
{
    private readonly IPrefixSetupRunner _runner;
    private readonly GameInstallation _installation;
    private readonly IReadOnlyList<PrefixComponentId> _components;
    private CancellationTokenSource? _run;

    /// <param name="plan">The dry run's plan, which the dialog previews.</param>
    /// <param name="components">What to set up, in install order.</param>
    public PrefixSetupViewModel(IPrefixSetupRunner runner, GameInstallation installation, PrefixSetupPlan plan, IReadOnlyList<PrefixComponentId> components)
    {
        _runner = runner;
        _installation = installation;
        _components = components;

        GameName = installation.Game.DisplayName;
        PrefixPath = plan.Prefix.Path.ToString();
        BackupPath = plan.BackupDirectory?.ToString() ?? "";
        SharedNote = plan.SharedNote ?? "";
        Steps = plan.Steps.Select(Preview).ToList();
        ConfirmationText = string.Format(Language.PrefixSetup_Confirmation, PrefixPath);

        var confirmed = this.WhenAnyValue(vm => vm.IsConfirmed);
        var idle = this.WhenAnyValue(vm => vm.IsRunning, vm => vm.Succeeded, vm => vm.HasFailed);
        RunCommand = ReactiveCommand.CreateFromTask(RunAsync, confirmed.CombineLatest(idle, (ok, s) => ok && !s.Item1 && !s.Item2 && !s.Item3));
        RetryCommand = ReactiveCommand.CreateFromTask(RunAsync, confirmed.CombineLatest(idle, (ok, s) => ok && !s.Item1 && s.Item3));
        CloseCommand = ReactiveCommand.Create(() => Complete(Unit.Default));
    }

    public string GameName { get; }
    public string PrefixPath { get; }
    public string BackupPath { get; }
    public string SharedNote { get; }
    public IReadOnlyList<PrefixPreviewStep> Steps { get; }
    public string ConfirmationText { get; }
    public ObservableCollection<string> Progress { get; } = [];
    [Reactive] public partial bool IsConfirmed { get; set; }
    [Reactive] public partial bool IsRunning { get; private set; }
    [Reactive] public partial bool Succeeded { get; private set; }
    [Reactive] public partial bool HasFailed { get; private set; }
    [Reactive] public partial string ErrorMessage { get; private set; } = "";
    public ReactiveCommand<Unit, Unit> RunCommand { get; }
    public ReactiveCommand<Unit, Unit> RetryCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    private async Task RunAsync()
    {
        using var cts = new CancellationTokenSource();
        _run = cts;
        Progress.Clear();
        ErrorMessage = "";
        HasFailed = false;
        IsRunning = true;
        try
        {
            // The runner's consent check compares this with the prefix it resolves itself, so a prefix that
            // moved since the preview is refused rather than changed.
            var request = new PrefixSetupRequest { Components = _components, Write = true, ConfirmedPrefixPath = PrefixPath };
            var result = await Task.Run(() => _runner.Run(_installation, request, cts.Token, ReportStep), cts.Token);

            if (result.Failure is { } failure)
            {
                ErrorMessage = string.Format(Language.PrefixSetup_StepFailed, failure.Name, failure.Error);
                HasFailed = true;
            }
            else
            {
                Succeeded = true;
            }
        }
        catch (PrefixSetupRefusedException refusal)
        {
            // A refusal (game still running, another window holds the lock) is fixed by the user, then Retry.
            ErrorMessage = refusal.Message;
            HasFailed = true;
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = Language.PrefixSetup_Cancelled;
            HasFailed = true;
        }
        finally
        {
            _run = null;
            IsRunning = false;
        }
    }

    private ValueTask ReportStep(PrefixSetupStepResult step)
        => new(Dispatcher.UIThread.InvokeAsync(() => Progress.Add($"{step.Name}: {Describe(step)}")).GetTask());

    private static string Describe(PrefixSetupStepResult step) => step.Status switch
    {
        PrefixSetupStepStatus.AlreadyInstalled => Language.PrefixSetup_AlreadyInstalled,
        PrefixSetupStepStatus.Installed => Language.PrefixSetup_Installed,
        PrefixSetupStepStatus.Failed => string.Format(Language.PrefixSetup_Failed, step.Error),
        _ => Language.PrefixSetup_NotRun,
    };

    private static PrefixPreviewStep Preview(PrefixSetupPlanEntry entry)
    {
        var lines = new List<string> { $"{entry.Detected.State}: {entry.Detected.Evidence}" };
        if (!entry.WillInstall)
        {
            lines.Add(Language.PrefixSetup_NothingToDo);
            return new PrefixPreviewStep(entry.Name, lines);
        }

        lines.AddRange(entry.Downloads.Select(d => string.Format(Language.PrefixSetup_Download, d.Url, d.Sha256)));
        lines.AddRange(entry.Actions);
        return new PrefixPreviewStep(entry.Name, lines);
    }

    /// <summary>
    /// Stops a run under way, so nothing keeps installing behind a dialog that is gone.
    /// </summary>
    public override void Close()
    {
        _run?.Cancel();
        base.Close();
    }
}
