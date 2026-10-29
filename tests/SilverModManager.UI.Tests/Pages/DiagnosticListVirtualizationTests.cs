using System.Reactive;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using ReactiveUI;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.App.UI.Controls.Diagnostics;
using SilverModManager.App.UI.Pages.Diagnostics;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;

namespace SilverModManager.UI.Tests.Pages;

public class DiagnosticListVirtualizationTests(IServiceProvider provider) : AUiTest(provider)
{
    private const int RealizedLimit = 60;

    private sealed class StubViewModel : APageViewModel<IDiagnosticListViewModel>, IDiagnosticListViewModel
    {
        private DiagnosticFilter _filter;
        private IDiagnosticEntryViewModel[] _entries;
        public StubViewModel(IDiagnosticEntryViewModel[] entries, int critical, int warnings, int suggestions) : base(new DesignWindowManager())
        {
            _entries = entries;
            NumCritical = critical;
            NumWarnings = warnings;
            NumSuggestions = suggestions;
        }

        public LoadoutId LoadoutId { get; set; }
        public IDiagnosticEntryViewModel[] DiagnosticEntries { get => _entries; set => this.RaiseAndSetIfChanged(ref _entries, value); }
        public int NumCritical { get; }
        public int NumWarnings { get; }
        public int NumSuggestions { get; }
        public DiagnosticFilter Filter { get => _filter; set => this.RaiseAndSetIfChanged(ref _filter, value); }
        public ReactiveCommand<Unit, Unit> RefreshCommand { get; } = ReactiveCommand.Create(() => { });
        public bool IsRefreshing => false;
        public ReactiveCommand<Unit, Unit> CopySupportBundleCommand { get; } = ReactiveCommand.Create(() => { });
        public string SupportBundleMessage => "";
    }

    private static IDiagnosticEntryViewModel[] Entries(int count) => Enumerable.Range(0, count)
        .Select(i => (IDiagnosticEntryViewModel)new DiagnosticEntryDesignViewModel(new Diagnostic
        {
            Id = new DiagnosticId(),
            Title = $"Entry {i}",
            Severity = i % 3 == 0 ? DiagnosticSeverity.Suggestion : i % 3 == 1 ? DiagnosticSeverity.Warning : DiagnosticSeverity.Critical,
            Summary = DiagnosticMessage.From($"Summary {i}"),
            Details = DiagnosticMessage.DefaultValue,
            DataReferences = new Dictionary<DataReferenceDescription, IDataReference>(),
        }))
        .ToArray();

    private async Task<(StubViewModel Vm, DiagnosticListView View)> ShowAsync(int count, int critical = 334, int warnings = 333, int suggestions = 333)
    {
        var vm = new StubViewModel(Entries(count), critical, warnings, suggestions);
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var view = new DiagnosticListView { ViewModel = vm };
            Track(new Window { Width = 800, Height = 400, Content = view }).Show();
            Dispatcher.UIThread.RunJobs();
            return (vm, view);
        });
    }

    private static ItemsControl List(DiagnosticListView view) =>
        view.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "HealthCheckItemsControl");

    private static int RealizedRows(DiagnosticListView view) =>
        view.GetVisualDescendants().OfType<DiagnosticEntryView>().Count();

    [Fact]
    public async Task Test_AThousandEntries_RealizeOnlyTheRowsInView()
    {
        var (_, view) = await ShowAsync(1000);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var list = List(view);
            var scroller = list.FindAncestorOfType<ScrollViewer>()!;
            scroller.Extent.Height.Should().BeGreaterThan(scroller.Viewport.Height);
            RealizedRows(view).Should().BeGreaterThan(0).And.BeLessThan(RealizedLimit);
            list.ItemsPanelRoot.Should().BeOfType<VirtualizingStackPanel>();
        });
    }

    [Fact]
    public async Task Test_ScrollingToTheEnd_RealizesTheLastRowAndRecyclesTheFirst()
    {
        var (_, view) = await ShowAsync(1000);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var list = List(view);
            list.FindAncestorOfType<ScrollViewer>()!.ScrollToEnd();
            Dispatcher.UIThread.RunJobs();
            list.ContainerFromIndex(999).Should().NotBeNull();
            list.ContainerFromIndex(0).Should().BeNull();
            RealizedRows(view).Should().BeLessThan(RealizedLimit);

            var last = list.ContainerFromIndex(999)!.GetVisualDescendants().OfType<DiagnosticEntryView>().Single();
            last.FindControl<TextBlock>("TitleText")!.Text.Should().Be("Entry 999");
            last.FindControl<Control>("SeverityIcon")!.Classes.Should().Contain("Suggestion").And.NotContain(["Warning", "Critical"]);

            // Every realized row, recycled or not, shows its own entry.
            foreach (var row in view.GetVisualDescendants().OfType<DiagnosticEntryView>())
            {
                row.FindControl<TextBlock>("TitleText")!.Text.Should().Be(row.ViewModel!.Title);
                var classes = row.FindControl<Control>("SeverityIcon")!.Classes;
                var own = row.ViewModel.Severity.ToString();
                classes.Should().Contain(own);
                classes.Should().NotContain(new[] { "Suggestion", "Warning", "Critical" }.Where(c => c != own));
            }
        });
    }

    [Fact]
    public async Task Test_SwitchingTabs_KeepsTheListVirtualizedAndSetsTheFilter()
    {
        var (vm, view) = await ShowAsync(1000);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var tabs = view.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "TabControl");
            var expected = new[]
            {
                DiagnosticFilter.Suggestions | DiagnosticFilter.Warnings | DiagnosticFilter.Critical,
                DiagnosticFilter.Suggestions,
                DiagnosticFilter.Warnings,
                DiagnosticFilter.Critical,
            };
            for (var i = 0; i < expected.Length; i++)
            {
                tabs.SelectedIndex = i;
                Dispatcher.UIThread.RunJobs();
                vm.Filter.Should().Be(expected[i]);
                RealizedRows(view).Should().BeGreaterThan(0).And.BeLessThan(RealizedLimit);
            }
        });
    }

    [Fact]
    public async Task Test_TabHeaders_ShowTheCounts()
    {
        var (_, view) = await ShowAsync(1000, critical: 0, warnings: 789, suggestions: 2);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Header(view, "AllTab").Should().Be(string.Format(Language.DiagnosticListView_DiagnosticListView_All, 791));
            Header(view, "SuggestionTab").Should().Be(string.Format(Language.DiagnosticListView_DiagnosticListView_Suggestions, 2));
            Header(view, "WarningTab").Should().Be(string.Format(Language.DiagnosticListView_DiagnosticListView_Warnings, 789));
            Header(view, "CriticalTab").Should().Be(string.Format(Language.DiagnosticListView_DiagnosticListView_Critical, 0));
        });
    }

    private static object? Header(DiagnosticListView view, string name) =>
        view.GetVisualDescendants().OfType<TabItem>().Single(t => t.Name == name).Header;

    [Fact]
    public async Task Test_ChangingTheEntry_LeavesOnlyTheNewSeverityClass()
    {
        var entries = Entries(3); // index 1 is a Warning, index 2 is Critical
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var row = new DiagnosticEntryView { ViewModel = entries[1] };
            Track(new Window { Width = 800, Height = 200, Content = row }).Show();
            Dispatcher.UIThread.RunJobs();
            var icon = row.FindControl<Control>("SeverityIcon")!;
            icon.Classes.Should().Contain("Warning");

            row.ViewModel = entries[2];
            Dispatcher.UIThread.RunJobs();
            icon.Classes.Should().Contain("Critical").And.NotContain("Warning");
            row.FindControl<TextBlock>("TitleText")!.Text.Should().Be("Entry 2");
        });
    }

    private static Diagnostic[] Diagnostics(int count, string prefix = "Entry") => Enumerable.Range(0, count)
        .Select(i => new Diagnostic
        {
            Id = new DiagnosticId(),
            Title = $"{prefix} {i}",
            Severity = DiagnosticSeverity.Warning,
            Summary = DiagnosticMessage.From($"Summary {i}"),
            Details = DiagnosticMessage.DefaultValue,
            DataReferences = new Dictionary<DataReferenceDescription, IDataReference>(),
        })
        .ToArray();

    private static IDiagnosticEntryViewModel[] Wrap(Diagnostic[] diagnostics) =>
        diagnostics.Select(d => (IDiagnosticEntryViewModel)new DiagnosticEntryDesignViewModel(d)).ToArray();

    [Fact]
    public async Task Test_ReplacingTheEntriesWithTheSameDiagnostics_KeepsTheScrollOffset()
    {
        var diagnostics = Diagnostics(1000);
        var (vm, view) = await ShowAsync(0);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm.DiagnosticEntries = Wrap(diagnostics);
            Dispatcher.UIThread.RunJobs();
            var scroller = List(view).FindAncestorOfType<ScrollViewer>()!;
            scroller.Offset = new Avalonia.Vector(0, 3000);
            Dispatcher.UIThread.RunJobs();
            scroller.Offset.Y.Should().Be(3000);

            vm.DiagnosticEntries = Wrap(diagnostics);
            Dispatcher.UIThread.RunJobs();

            scroller.Offset.Y.Should().Be(3000, "a refresh that finds the same diagnostics must not send the list back to the top");
        });
    }

    [Fact]
    public async Task Test_ReplacingTheEntriesWithTheSameDiagnosticsAtTheEnd_KeepsTheScrollOffset()
    {
        var diagnostics = Diagnostics(1000);
        var (vm, view) = await ShowAsync(0);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm.DiagnosticEntries = Wrap(diagnostics);
            Dispatcher.UIThread.RunJobs();
            var scroller = List(view).FindAncestorOfType<ScrollViewer>()!;
            scroller.ScrollToEnd();
            Dispatcher.UIThread.RunJobs();
            var end = scroller.Offset.Y;
            end.Should().BeGreaterThan(0);

            vm.DiagnosticEntries = Wrap(diagnostics);
            Dispatcher.UIThread.RunJobs();

            scroller.Offset.Y.Should().Be(end);
        });
    }

    [Fact]
    public async Task Test_AnItemsSourceSwapThatResetsTheScroller_IsUndoneForTheSameDiagnostics()
    {
        // Headless Avalonia keeps the offset across an ItemsSource swap by itself, so the restore in the view would
        // go unnoticed. The swap here resets the offset the way a layout pass on the real list can, and the view has
        // to put the user's place back.
        var diagnostics = Diagnostics(1000);
        var (vm, view) = await ShowAsync(0);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm.DiagnosticEntries = Wrap(diagnostics);
            Dispatcher.UIThread.RunJobs();
            var list = List(view);
            var scroller = list.FindAncestorOfType<ScrollViewer>()!;
            scroller.Offset = new Avalonia.Vector(0, 3000);
            Dispatcher.UIThread.RunJobs();
            list.PropertyChanged += (_, e) =>
            {
                if (e.Property == ItemsControl.ItemsSourceProperty) scroller.Offset = default;
            };

            vm.DiagnosticEntries = Wrap(diagnostics);
            Dispatcher.UIThread.RunJobs();

            scroller.Offset.Y.Should().Be(3000);
        });
    }

    [Fact]
    public async Task Test_ReplacingTheEntriesWithOtherDiagnostics_ScrollsBackToTheTop()
    {
        var (vm, view) = await ShowAsync(0);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm.DiagnosticEntries = Wrap(Diagnostics(1000));
            Dispatcher.UIThread.RunJobs();
            var scroller = List(view).FindAncestorOfType<ScrollViewer>()!;
            scroller.Offset = new Avalonia.Vector(0, 3000);
            Dispatcher.UIThread.RunJobs();

            vm.DiagnosticEntries = Wrap(Diagnostics(1000, "Other"));
            Dispatcher.UIThread.RunJobs();

            scroller.Offset.Y.Should().Be(0);
        });
    }
}
