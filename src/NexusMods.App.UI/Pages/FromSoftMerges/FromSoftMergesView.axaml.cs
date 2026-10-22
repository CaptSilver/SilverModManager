using Avalonia.Controls;
using Avalonia.ReactiveUI;
using JetBrains.Annotations;
using NexusMods.Games.FromSoft.Merge;
using ReactiveUI;
using R3;

namespace NexusMods.App.UI.Pages.FromSoftMerges;

[UsedImplicitly]
public partial class FromSoftMergesView : ReactiveUserControl<IFromSoftMergesViewModel>
{
    public FromSoftMergesView()
    {
        InitializeComponent();

        // Wired once here rather than on activation, so reopening the tab does not stack handlers.
        EntriesList.SelectionChanged += (_, _) =>
        {
            if (ViewModel is { } vm) vm.Selected = EntriesList.SelectedIndex >= 0 && EntriesList.SelectedIndex < vm.Entries.Count ? vm.Entries[EntriesList.SelectedIndex] : null;
        };

        this.WhenActivated(disposables =>
        {
            if (ViewModel is not { } viewModel) return;

            this.BindCommand(ViewModel, vm => vm.RefreshCommand, view => view.RefreshButton).AddTo(disposables);

            // The list is replaced as a whole, so rows are strings and selection is by index.
            viewModel.WhenAnyValue(vm => vm.Entries)
                .Subscribe(entries => EntriesList.ItemsSource = entries.Select(Describe).ToArray())
                .AddTo(disposables);
            viewModel.WhenAnyValue(vm => vm.Selected, vm => vm.Entries,
                    (selected, entries) => selected is null ? -1 : entries.ToList().FindIndex(e => e.Path == selected.Path))
                .Subscribe(index => EntriesList.SelectedIndex = index)
                .AddTo(disposables);

            viewModel.WhenAnyValue(vm => vm.Problem).Subscribe(text => Show(ProblemText, text)).AddTo(disposables);
            viewModel.WhenAnyValue(vm => vm.Refusal).Subscribe(text => Show(RefusalText, text)).AddTo(disposables);
            viewModel.WhenAnyValue(vm => vm.EmptyState).Subscribe(text => Show(EmptyText, text)).AddTo(disposables);
            viewModel.WhenAnyValue(vm => vm.PriorityOrderHint).Subscribe(text => Show(HintText, text)).AddTo(disposables);

            viewModel.WhenAnyValue(vm => vm.Selected, vm => vm.SelectedNotes)
                .Subscribe(state =>
                {
                    var (selected, notes) = state;
                    DetailPanel.IsVisible = selected is not null;
                    DetailTitle.Text = selected?.Path;
                    Show(VanillaText, selected is { VanillaSource: not "" } ? $"Vanilla source: {selected.VanillaSource}" : "");
                    NotesPanel.Children.Clear();
                    foreach (var note in notes) NotesPanel.Children.Add(new TextBlock { Text = note, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
                })
                .AddTo(disposables);
        });
    }

    private static void Show(TextBlock block, string? text)
    {
        block.Text = text;
        block.IsVisible = !string.IsNullOrEmpty(text);
    }

    private static string Describe(MergeReportEntry entry)
    {
        var outcome = entry.Outcome switch
        {
            MergeReportOutcome.Merged => "merged",
            MergeReportOutcome.Refused => "refused",
            MergeReportOutcome.NotMergeable => "not merged",
            _ => throw new ArgumentOutOfRangeException(nameof(entry), entry.Outcome, "Unknown merge outcome"),
        };
        return $"{entry.Path}  {(entry.Strategy == "" ? "no strategy" : entry.Strategy)}  {outcome}  {string.Join(", ", entry.Contributors)}";
    }
}
