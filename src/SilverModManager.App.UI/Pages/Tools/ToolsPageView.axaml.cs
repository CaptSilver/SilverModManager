using Avalonia.Controls;
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

        // Hooked once here: the page is activated again each time it is shown, and handlers added on activation
        // would stack up and save the same program more than once.
        BrowseUserToolButton.Click += async (_, _) => await BrowseForProgram();
        SaveUserToolButton.Click += async (_, _) => await SaveUserTool();
        CancelUserToolEditButton.Click += (_, _) => ResetForm();

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
