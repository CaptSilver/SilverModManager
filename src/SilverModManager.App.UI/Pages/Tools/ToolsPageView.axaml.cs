using Avalonia.Controls;
using Avalonia.Threading;
using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Platform.Proton.Tools;
using Avalonia.Platform.Storage;
using Avalonia.ReactiveUI;
using JetBrains.Annotations;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls;
using SilverModManager.Sdk.Games;
using R3;
using ReactiveUI;
using SilverModManager.UI.Sdk;

namespace SilverModManager.App.UI.Pages.Tools;

[UsedImplicitly]
public partial class ToolsPageView : ReactiveUserControl<IToolsPageViewModel>
{
    // The tool being edited, or null while the form adds a new one.
    private UserTool.ReadOnly? _editing;

    public ToolsPageView()
    {
        InitializeComponent();

        UserToolRunViaBox.ItemsSource = Enum.GetValues<ToolRunVia>();
        UserToolRunViaBox.SelectedItem = ToolRunVia.Auto;
        CancelUserToolEditButton.IsVisible = false;
        CancelRunButton.IsVisible = false;
        RunningText.IsVisible = false;

        // Hooked once here: the page is activated again each time it is shown, and handlers added on activation
        // would stack up and save the same program more than once.
        BrowseUserToolButton.Click += async (_, _) => await BrowseForProgram();
        SaveUserToolButton.Click += async (_, _) => await SaveUserTool();
        CancelUserToolEditButton.Click += (_, _) => ResetForm();
        AddToolFolderButton.Click += (_, _) => ViewModel?.AddToolCommand.Execute(true);
        AddToolProgramButton.Click += (_, _) => ViewModel?.AddToolCommand.Execute(false);
        CancelRunButton.Click += (_, _) => ViewModel?.CancelRunCommand.Execute(R3.Unit.Default);

        this.WhenActivated(disposables =>
        {
            this.BindCommand(ViewModel, vm => vm.RunWindowsProgramCommand, view => view.RunWindowsProgramButton)
                .AddTo(disposables);

            this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.UserTools))
                .Subscribe(RebuildUserToolRows)
                .AddTo(disposables);

            this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.Tools))
                .Subscribe(RebuildToolRows)
                .AddTo(disposables);

            this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.CatalogueTools, x => x.Plugins))
                .Subscribe(state => RebuildCatalogueRows(state.Item1, state.Item2))
                .AddTo(disposables);

            this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.Pipelines))
                .Subscribe(RebuildPipelineRows)
                .AddTo(disposables);

            // The running line is refreshed from a timer thread, so it is posted to the UI thread.
            this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.IsRunning, x => x.RunningMessage))
                .Subscribe(state => Dispatcher.UIThread.Post(() =>
                {
                    Show(RunningText, state.Item2);
                    CancelRunButton.IsVisible = state.Item1;
                }))
                .AddTo(disposables);

            this.WhenViewModelValue(vm => vm.WhenAnyValue(
                    x => x.CanRunWindowsProgram,
                    x => x.Diagnostic,
                    x => x.PrefixPath,
                    x => x.Strategy))
                .Subscribe(state =>
                {
                    var (canRun, diagnostic, prefixPath, strategy) = state;
                    Show(DiagnosticText, !canRun ? diagnostic : null);
                    Show(PrefixText, prefixPath is null ? null : $"Prefix: {prefixPath}");
                    Show(StrategyText, canRun && strategy is not null ? $"Runs with: {strategy}" : null);
                })
                .AddTo(disposables);

            this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.LastRunMessage))
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
        // Catalogue tools have their own section, with their prerequisites and cleaning mode.
        foreach (var tool in tools.Where(t => t is not ModdingTool { CatalogueId: not null }))
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

    private void RebuildCatalogueRows(IReadOnlyList<CatalogueToolRow> tools, IReadOnlyList<string> plugins)
    {
        CatalogueToolsPanel.Children.Clear();
        foreach (var row in tools)
        {
            var run = new StandardButton { Text = "Run", Type = StandardButton.Types.Secondary };
            run.Click += (_, _) => ViewModel?.RunToolCommand.Execute(row.Tool);

            var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(run);
            if (row.Row is { } saved)
            {
                var remove = new StandardButton { Text = "Remove", Type = StandardButton.Types.Secondary };
                remove.Click += async (_, _) => await Guarded(async () =>
                {
                    if (ViewModel is { } viewModel) await viewModel.RemoveUserTool(saved);
                });
                buttons.Children.Add(remove);
            }

            var header = new DockPanel();
            DockPanel.SetDock(buttons, Dock.Right);
            header.Children.Add(buttons);
            header.Children.Add(new TextBlock { Text = row.Name, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });

            var card = new StackPanel { Spacing = 4 };
            card.Children.Add(header);
            if (row.PrerequisiteMessage is { } needs)
                card.Children.Add(new TextBlock { Text = needs, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            if (row.CanQuickAutoClean && plugins.Count > 0) card.Children.Add(QuickAutoCleanPicker(row, plugins));
            CatalogueToolsPanel.Children.Add(card);
        }
    }

    private StackPanel QuickAutoCleanPicker(CatalogueToolRow row, IReadOnlyList<string> plugins)
    {
        var picker = new ComboBox { ItemsSource = plugins, SelectedIndex = 0, MinWidth = 240 };
        var clean = new StandardButton { Text = "Quick Auto Clean", Type = StandardButton.Types.Secondary };
        clean.Click += (_, _) =>
        {
            if (picker.SelectedItem is string plugin) ViewModel?.QuickAutoCleanCommand.Execute(new QuickAutoCleanRequest(row.Tool, plugin));
        };

        var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(picker);
        panel.Children.Add(clean);
        return panel;
    }

    private void RebuildPipelineRows(IReadOnlyList<ToolPipeline> pipelines)
    {
        PipelinesPanel.Children.Clear();
        PipelinesHeading.IsVisible = pipelines.Count > 0;
        foreach (var pipeline in pipelines)
        {
            var run = new StandardButton { Text = "Run", Type = StandardButton.Types.Secondary };
            run.Click += (_, _) => ViewModel?.RunPipelineCommand.Execute(pipeline);

            var row = new DockPanel();
            DockPanel.SetDock(run, Dock.Right);
            row.Children.Add(run);
            row.Children.Add(new TextBlock
            {
                Text = $"{pipeline.Name}: {string.Join(", ", pipeline.Steps.Select(step => step.Value))}",
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            });
            PipelinesPanel.Children.Add(row);
        }
    }

    private void RebuildUserToolRows(IReadOnlyList<UserTool.ReadOnly> tools)
    {
        UserToolsPanel.Children.Clear();
        foreach (var tool in tools)
        {
            var remove = new StandardButton { Text = "Remove", Type = StandardButton.Types.Secondary };
            remove.Click += async (_, _) => await Guarded(async () =>
            {
                // Read when the button is pressed: the row outlives the view model it was built from.
                if (ViewModel is not { } viewModel) return;
                await viewModel.RemoveUserTool(tool);
                // Otherwise the form stays on a program that no longer exists and every save is refused.
                if (_editing?.Id == tool.Id) ResetForm();
            });

            var edit = new StandardButton { Text = "Edit", Type = StandardButton.Types.Secondary };
            edit.Click += (_, _) => BeginEdit(tool);

            var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(edit);
            buttons.Children.Add(remove);

            var row = new DockPanel();
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons);
            row.Children.Add(new TextBlock
            {
                Text = $"{tool.Name} ({tool.RunVia}): {tool.Exe}",
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            });
            UserToolsPanel.Children.Add(row);
        }
    }

    private void BeginEdit(UserTool.ReadOnly tool)
    {
        _editing = tool;
        UserToolNameBox.Text = tool.Name;
        UserToolExeBox.Text = tool.Exe;
        UserToolArgumentsBox.Text = tool.Arguments;
        UserToolRunViaBox.SelectedItem = tool.RunVia;
        SaveUserToolButton.Text = "Save changes";
        CancelUserToolEditButton.IsVisible = true;
    }

    private void ResetForm()
    {
        _editing = null;
        UserToolNameBox.Text = "";
        UserToolExeBox.Text = "";
        UserToolArgumentsBox.Text = "";
        UserToolRunViaBox.SelectedItem = ToolRunVia.Auto;
        SaveUserToolButton.Text = "Add program";
        CancelUserToolEditButton.IsVisible = false;
    }

    private async Task BrowseForProgram()
    {
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider is null) return;

        var picked = await provider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose a program", AllowMultiple = false });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path) UserToolExeBox.Text = path;
    }

    private async Task SaveUserTool()
    {
        var draft = new UserToolDraft(
            UserToolNameBox.Text ?? "",
            UserToolExeBox.Text?.Trim() ?? "",
            UserToolArgumentsBox.Text ?? "",
            UserToolRunViaBox.SelectedItem is ToolRunVia via ? via : ToolRunVia.Auto);

        var editing = _editing;
        await Guarded(async () =>
        {
            if (ViewModel is not { } viewModel) return;
            if (editing is { } tool) await viewModel.UpdateUserTool(tool, draft);
            else await viewModel.AddUserTool(draft);
            ResetForm();
        });
    }

    // Bad input is the user's to fix, so it is shown on the page rather than thrown out of an event handler.
    private async Task Guarded(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ArgumentException ex)
        {
            Show(LastRunText, ex.Message);
        }
    }
}
