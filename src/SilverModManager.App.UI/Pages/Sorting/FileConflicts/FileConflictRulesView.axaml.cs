using Avalonia;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.ReactiveUI;
using JetBrains.Annotations;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.Sorting;

[UsedImplicitly]
public partial class FileConflictRulesView : ReactiveUserControl<IFileConflictRulesViewModel>
{
    public FileConflictRulesView()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            if (ViewModel is not { } viewModel) return;

            AddAfterButton.Command = viewModel.AddAfterCommand;
            AddBeforeButton.Command = viewModel.AddBeforeCommand;
            CandidatePicker.ItemsSource = viewModel.Candidates;

            CandidatePicker.GetObservable(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty)
                .Subscribe(item => viewModel.SelectedCandidate = item as FileConflictRuleCandidate)
                .DisposeWith(disposables);

            // Rows are replaced as a whole after an edit, which finishes off the UI thread.
            viewModel.WhenAnyValue(vm => vm.Rows)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(rows =>
                {
                    RulesList.ItemsSource = rows;
                    NoRulesText.IsVisible = rows.Count == 0;
                })
                .DisposeWith(disposables);

            viewModel.WhenAnyValue(vm => vm.Error)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(error =>
                {
                    ErrorText.Text = error;
                    ErrorText.IsVisible = !string.IsNullOrEmpty(error);
                })
                .DisposeWith(disposables);
        });
    }
}
