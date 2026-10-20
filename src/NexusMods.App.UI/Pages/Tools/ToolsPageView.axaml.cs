using Avalonia.Controls;
using Avalonia.ReactiveUI;
using JetBrains.Annotations;
using NexusMods.App.UI.Controls;
using R3;
using ReactiveUI;

namespace NexusMods.App.UI.Pages.Tools;

[UsedImplicitly]
public partial class ToolsPageView : ReactiveUserControl<IToolsPageViewModel>
{
    public ToolsPageView()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            this.BindCommand(ViewModel, vm => vm.RunWindowsProgramCommand, view => view.RunWindowsProgramButton)
                .AddTo(disposables);

            this.WhenAnyValue(view => view.ViewModel!.Tools)
                .Subscribe(RebuildToolRows)
                .AddTo(disposables);

            this.WhenAnyValue(
                    view => view.ViewModel!.CanRunWindowsProgram,
                    view => view.ViewModel!.Diagnostic,
                    view => view.ViewModel!.PrefixPath,
                    view => view.ViewModel!.Strategy)
                .Subscribe(state =>
                {
                    var (canRun, diagnostic, prefixPath, strategy) = state;
                    Show(DiagnosticText, !canRun ? diagnostic : null);
                    Show(PrefixText, prefixPath is null ? null : $"Prefix: {prefixPath}");
                    Show(StrategyText, canRun && strategy is not null ? $"Runs with: {strategy}" : null);
                })
                .AddTo(disposables);

            this.WhenAnyValue(view => view.ViewModel!.LastRunMessage)
                .Subscribe(message => Show(LastRunText, message))
                .AddTo(disposables);
        });
    }

    private static void Show(TextBlock block, string? text)
    {
        block.Text = text;
        block.IsVisible = !string.IsNullOrEmpty(text);
    }

    // The set of tools only changes with the loadout, so rows are rebuilt instead of templated.
    private void RebuildToolRows(IReadOnlyList<Abstractions.Loadouts.ITool> tools)
    {
        ToolsPanel.Children.Clear();
        foreach (var tool in tools)
        {
            var button = new StandardButton { Text = "Run", Type = StandardButton.Types.Secondary };
            button.Click += (_, _) => ViewModel?.RunToolCommand.Execute(tool);

            var row = new DockPanel();
            DockPanel.SetDock(button, Dock.Right);
            row.Children.Add(button);
            row.Children.Add(new TextBlock { Text = tool.Name, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            ToolsPanel.Children.Add(row);
        }
    }
}
