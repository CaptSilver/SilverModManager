using System.Reactive;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using ReactiveUI;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Controls.MarkdownRenderer;
using SilverModManager.App.UI.Pages.Diagnostics;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;

namespace SilverModManager.UI.Tests;

public class DiagnosticDetailsViewTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class StubViewModel : APageViewModel<IDiagnosticDetailsViewModel>, IDiagnosticDetailsViewModel
    {
        public StubViewModel(string markdown, IReadOnlyList<DiagnosticActionButton> actions, string? moreDetails = null) : base(new DesignWindowManager())
        {
            MarkdownRendererViewModel = new MarkdownRendererViewModel { Contents = markdown };
            Actions = actions;
            if (moreDetails is not null) MoreDetailsRendererViewModel = new MarkdownRendererViewModel { Contents = moreDetails };
        }

        public IMarkdownRendererViewModel? MoreDetailsRendererViewModel { get; }

        public DiagnosticSeverity Severity => DiagnosticSeverity.Warning;
        public IMarkdownRendererViewModel MarkdownRendererViewModel { get; }
        public IReadOnlyList<DiagnosticActionButton> Actions { get; }
    }

    private static string LongMarkdown()
    {
        var builder = new StringBuilder("# Long details\n\n");
        for (var i = 0; i < 60; i++) builder.Append("- item ").Append(i).Append('\n');
        return builder.ToString();
    }

    private static async Task<(Window Window, DiagnosticDetailsView View)> ShowAsync(string markdown, string? moreDetails = null, string? note = null)
    {
        var action = new DiagnosticActionButton("Fix it", ReactiveCommand.Create(() => { }), note);
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var view = new DiagnosticDetailsView { ViewModel = new StubViewModel(markdown, [action], moreDetails) };
            var window = new Window { Width = 800, Height = 300, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            return (window, view);
        });
    }

    [Fact]
    public async Task Test_LongDetails_ScrollInsteadOfBeingCutOff()
    {
        var (window, view) = await ShowAsync(LongMarkdown());
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var actions = view.FindControl<ItemsControl>("ActionsItemsControl")!;
            var scroller = actions.FindAncestorOfType<ScrollViewer>();
            scroller.Should().NotBeNull("the details and the actions must sit inside a scroller");
            scroller!.Extent.Height.Should().BeGreaterThan(scroller.Viewport.Height);
            scroller.VerticalScrollBarVisibility.Should().Be(Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
            scroller.HorizontalScrollBarVisibility.Should().Be(Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);

            var header = view.FindControl<TextBlock>("TitleTextBlock")!;
            header.FindAncestorOfType<ScrollViewer>().Should().BeNull("the header stays fixed above the scrolled body");

            // exactly one scrollbar: the markdown renderer's own scroller must not take height of its own
            view.GetVisualDescendants().OfType<ScrollViewer>()
                .Count(s => s.Extent.Height > s.Viewport.Height + 0.5)
                .Should().Be(1);
        });
        await Dispatcher.UIThread.InvokeAsync(window.Close);
    }

    [Fact]
    public async Task Test_ScrollingToTheEnd_BringsTheActionsIntoView()
    {
        var (window, view) = await ShowAsync(LongMarkdown());
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var actions = view.FindControl<ItemsControl>("ActionsItemsControl")!;
            var scroller = actions.FindAncestorOfType<ScrollViewer>()!;

            bool ActionsVisible()
            {
                var bounds = actions.TranslatePoint(new Point(0, 0), scroller)!.Value;
                return bounds.Y >= 0 && bounds.Y + actions.Bounds.Height <= scroller.Viewport.Height + 0.5;
            }

            ActionsVisible().Should().BeFalse("the actions start below the fold");
            scroller.ScrollToEnd();
            Dispatcher.UIThread.RunJobs();
            scroller.Offset.Y.Should().BeApproximately(scroller.Extent.Height - scroller.Viewport.Height, 0.5);
            ActionsVisible().Should().BeTrue();
        });
        await Dispatcher.UIThread.InvokeAsync(window.Close);
    }

    [Fact]
    public async Task Test_ActionButtons_ArePressablePrimaryButtons()
    {
        var (window, view) = await ShowAsync("Some details");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var button = view.GetVisualDescendants().OfType<StandardButton>().Single();
            button.IsEnabled.Should().BeTrue("a disabled button is drawn greyed out");
            button.IsEffectivelyEnabled.Should().BeTrue();
            button.GetValue(StandardButton.TypeProperty).Should().Be(StandardButton.Types.Primary);
            button.GetValue(StandardButton.FillProperty).Should().Be(StandardButton.Fills.Strong);
            button.Opacity.Should().Be(1);
            button.Background.Should().BeAssignableTo<Avalonia.Media.ISolidColorBrush>()
                .Which.Opacity.Should().Be(1);
        });
        await Dispatcher.UIThread.InvokeAsync(window.Close);
    }

    [Fact]
    public async Task Test_MoreDetails_AreHiddenUntilTheToggleIsPressed()
    {
        var (window, view) = await ShowAsync("Plain text", moreDetails: "THE COMMAND LINE");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var host = view.FindControl<Control>("MoreDetailsHost")!;
            var toggle = view.FindControl<Button>("MoreDetailsToggle")!;
            toggle.IsVisible.Should().BeTrue();
            toggle.IsEffectivelyVisible.Should().BeTrue();
            host.IsVisible.Should().BeFalse("the technical details start collapsed");

            toggle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            host.IsVisible.Should().BeTrue();
            ((IDiagnosticDetailsViewModel)view.ViewModel!).MoreDetailsRendererViewModel!.Contents.Should().Contain("THE COMMAND LINE");

            toggle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            host.IsVisible.Should().BeFalse();
        });
        await Dispatcher.UIThread.InvokeAsync(window.Close);
    }

    [Fact]
    public async Task Test_WithoutMoreDetails_ThereIsNoToggle()
    {
        var (window, view) = await ShowAsync("Plain text");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            view.FindControl<Button>("MoreDetailsToggle")!.IsVisible.Should().BeFalse();
            view.FindControl<Control>("MoreDetailsHost")!.IsVisible.Should().BeFalse();
        });
        await Dispatcher.UIThread.InvokeAsync(window.Close);
    }

    [Fact]
    public async Task Test_AnActionNote_SitsNextToTheButton()
    {
        var (window, view) = await ShowAsync("Plain text", note: "Steam is closed and reopened if it is running.");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            view.GetVisualDescendants().OfType<TextBlock>()
                .Should().Contain(t => t.Text == "Steam is closed and reopened if it is running." && t.IsEffectivelyVisible);
        });
        await Dispatcher.UIThread.InvokeAsync(window.Close);
    }

    [Fact]
    public async Task Test_AnActionWithoutANote_ShowsNoNoteText()
    {
        var (window, view) = await ShowAsync("Plain text");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == "ActionNoteTextBlock")
                .Should().OnlyContain(t => !t.IsVisible);
        });
        await Dispatcher.UIThread.InvokeAsync(window.Close);
    }

    private sealed class NoopHandler(string actionId) : IDiagnosticActionHandler
    {
        public string ActionId { get; } = actionId;
        public Task ExecuteAsync(SilverModManager.Sdk.Loadouts.LoadoutId loadoutId, System.Collections.Immutable.ImmutableArray<string> arguments) => Task.CompletedTask;
    }

    private DispatcherOperation<DiagnosticDetailsViewModel> RealViewModel(Diagnostic diagnostic)
        => Dispatcher.UIThread.InvokeAsync(() => new DiagnosticDetailsViewModel(
            new DesignWindowManager(),
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IDiagnosticWriter>(Provider),
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IMarkdownRendererViewModel>(Provider),
            diagnostic,
            default,
            [new NoopHandler(DiagnosticActions.UseLoadoutFromSteamId)],
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DiagnosticDetailsViewModel>.Instance,
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IMarkdownRendererViewModel>(Provider)));

    [Fact]
    public async Task Test_TheViewModel_ShowsAnEnabledButtonWithTheActionsNote_AndKeepsMoreDetailsSeparate()
    {
        var diagnostic = new Diagnostic
        {
            Id = new DiagnosticId("SilverModManager.UI.Tests", 2),
            Title = "Title",
            Severity = DiagnosticSeverity.Suggestion,
            Summary = DiagnosticMessage.From("Summary"),
            Details = DiagnosticMessage.From("Plain consequence"),
            DataReferences = new Dictionary<SilverModManager.Abstractions.Diagnostics.References.DataReferenceDescription, SilverModManager.Abstractions.Diagnostics.References.IDataReference>(),
            Actions = [DiagnosticActions.UseLoadoutFromSteam()],
            MoreDetails = "`'/shim' run 'Main' -- %command%`",
        };

        var vm = await RealViewModel(diagnostic);
        Window? window = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window = new Window { Width = 800, Height = 600 };
            window.Content = new DiagnosticDetailsView { ViewModel = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            vm.MarkdownRendererViewModel.Contents.Should().Contain("Plain consequence").And.NotContain("%command%");
            vm.MoreDetailsRendererViewModel!.Contents.Should().Contain("%command%");
            vm.Actions.Single().Note.Should().Contain("reopened");

            var button = ((Control)window.Content).GetVisualDescendants().OfType<StandardButton>().Single();
            button.IsEffectivelyEnabled.Should().BeTrue();
            button.Command!.CanExecute(null).Should().BeTrue();
        });
        await Dispatcher.UIThread.InvokeAsync(window!.Close);
    }
}
