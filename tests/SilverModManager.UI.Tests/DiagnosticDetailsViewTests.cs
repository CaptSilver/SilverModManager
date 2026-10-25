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
        public StubViewModel(string markdown, IReadOnlyList<DiagnosticActionButton> actions) : base(new DesignWindowManager())
        {
            MarkdownRendererViewModel = new MarkdownRendererViewModel { Contents = markdown };
            Actions = actions;
        }

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

    private static async Task<(Window Window, DiagnosticDetailsView View)> ShowAsync(string markdown)
    {
        var action = new DiagnosticActionButton("Fix it", ReactiveCommand.Create(() => { }));
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var view = new DiagnosticDetailsView { ViewModel = new StubViewModel(markdown, [action]) };
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
}
