using System.Diagnostics;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.ReactiveUI;
using Avalonia.VisualTree;
using JetBrains.Annotations;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.App.UI.Controls.Diagnostics;
using SilverModManager.App.UI.Extensions;
using SilverModManager.App.UI.Resources;
using ReactiveUI;
using Reloaded.Memory.Extensions;

namespace SilverModManager.App.UI.Pages.Diagnostics;

[UsedImplicitly]
public partial class DiagnosticListView : ReactiveUserControl<IDiagnosticListViewModel>
{
    private DiagnosticRowKey[] _shownRows = [];

    private void ShowEntries(IDiagnosticEntryViewModel[] entries)
    {
        var scroller = HealthCheckItemsControl.FindAncestorOfType<ScrollViewer>();
        var offset = scroller?.Offset ?? default;
        var rows = DiagnosticListScroll.KeysOf(entries);
        var keepOffset = scroller is not null && DiagnosticListScroll.KeepsOffset(_shownRows, rows);
        _shownRows = rows;

        HealthCheckItemsControl.ItemsSource = entries;
        if (scroller is null) return;

        // The extent is only known after layout; without it the offset would be clamped to the old, empty extent.
        scroller.UpdateLayout();
        scroller.Offset = keepOffset ? offset : default;
    }

    public DiagnosticListView()
    {
        InitializeComponent();

        this.WhenActivated(disposable =>
            {
                // update headers whenever the counts change
                this.WhenAnyValue(
                        view => view.ViewModel!.NumCritical,
                        view => view.ViewModel!.NumWarnings,
                        view => view.ViewModel!.NumSuggestions
                    )
                    .Subscribe(counts =>
                        {
                            var (numCritical, numWarnings, numSuggestions) = counts;
                            var total = numCritical + numWarnings + numSuggestions;
                
                            // set tab headers
                            AllTab.Header = string.Format(Language.DiagnosticListView_DiagnosticListView_All, total);
                            SuggestionTab.Header = string.Format(Language.DiagnosticListView_DiagnosticListView_Suggestions, numSuggestions);
                            WarningTab.Header = string.Format(Language.DiagnosticListView_DiagnosticListView_Warnings, numWarnings);
                            CriticalTab.Header = string.Format(Language.DiagnosticListView_DiagnosticListView_Critical, numCritical);
                
                            EmptyState.IsActive = total == 0;
                        }
                    )
                    .DisposeWith(disposable);
                
                this.BindCommand(ViewModel, vm => vm.RefreshCommand, view => view.RefreshButton)
                    .DisposeWith(disposable);

                this.OneWayBind(ViewModel, vm => vm.IsRefreshing, view => view.RefreshButton.Text, isRefreshing => isRefreshing ? "Refreshing..." : "Refresh")
                    .DisposeWith(disposable);

                this.BindCommand(ViewModel, vm => vm.CopySupportBundleCommand, view => view.SupportBundleButton)
                    .DisposeWith(disposable);

                this.OneWayBind(ViewModel, vm => vm.SupportBundleMessage, view => view.SupportBundleText.Text)
                    .DisposeWith(disposable);

                // Every refresh replaces the entries. The same diagnostics (id, title and summary: an id only names the kind of diagnostic) keep the scroll offset; another set (a tab switch, a fix) starts at the top.
                this.WhenAnyValue(view => view.ViewModel)
                    .WhereNotNull()
                    .Select(viewModel => viewModel.WhenAnyValue(vm => vm.DiagnosticEntries))
                    .Switch()
                    .Subscribe(ShowEntries)
                    .DisposeWith(disposable);
                
                this.WhenAnyValue(view => view.TabControl.SelectedItem)
                    .Select(selectedItem =>
                    {
                        if (ReferenceEquals(selectedItem, AllTab)) return DiagnosticFilter.Suggestions | DiagnosticFilter.Warnings | DiagnosticFilter.Critical;
                        if (ReferenceEquals(selectedItem, SuggestionTab)) return DiagnosticFilter.Suggestions;
                        if (ReferenceEquals(selectedItem, WarningTab)) return DiagnosticFilter.Warnings;
                        if (ReferenceEquals(selectedItem, CriticalTab)) return DiagnosticFilter.Critical;
                        throw new UnreachableException();
                    })
                    .Subscribe(filter =>
                    {
                        ViewModel!.Filter = filter;
                    })
                    .DisposeWith(disposable);
            }
        );
    }
}
