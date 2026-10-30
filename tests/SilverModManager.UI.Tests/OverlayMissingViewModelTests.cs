using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using FluentAssertions;
using SilverModManager.App.UI.Overlays;
using SilverModManager.UI.Sdk;
using ReactiveUI;
using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.App.UI.Overlays.Generic.MessageBox.Ok;
using SilverModManager.App.UI.Overlays.Generic.MessageBox.OkCancel;
using SilverModManager.App.UI.Overlays.LibraryDeleteConfirmation;
using SilverModManager.App.UI.Overlays.Login;
using SilverModManager.App.UI.Pages.Library;

namespace SilverModManager.UI.Tests;

/// <summary>
/// Overlays and left menu items whose view model arrives after activation or is missing when a button is pressed:
/// they wait for it or do nothing, instead of dereferencing null.
/// </summary>
public class OverlayMissingViewModelTests(IServiceProvider provider) : AUiTest(provider)
{
    private abstract class FakeOverlay<TResult> : ReactiveObject, IOverlayViewModel<TResult>
    {
        public List<TResult> Completed { get; } = [];
        public ViewModelActivator Activator { get; } = new();
        public IOverlayController Controller { get; set; } = new DesignOverlayController();
        public Status Status { get; set; }
        public Task CompletionTask => Task.CompletedTask;
        public TResult? Result { get; set; }
        public void Close() { }
        public void Complete(TResult result) => Completed.Add(result);
    }

    private sealed class FakeOkCancel : FakeOverlay<bool>, IMessageBoxOkCancelViewModel;

    private sealed class FakeDeleteConfirmation : FakeOverlay<bool>, ILibraryItemDeleteConfirmationViewModel
    {
        public List<string> AllItems { get; } = [];
        public List<LibraryItemUsedLoadoutInfo> LoadoutsUsed { get; } = [];
    }

    private sealed class FakeNexusLogin : FakeOverlay<Unit>, INexusLoginOverlayViewModel
    {
        public R3.ReactiveCommand Cancel { get; } = new();
        public R3.ReactiveCommand<R3.Unit> UseApiKey { get; } = new();
        public Uri? Uri { get; } = new("https://example.com/login");
    }

    private static ReactiveCommand<Unit, Unit> CommandOf(Control view, string buttonName) =>
        (ReactiveCommand<Unit, Unit>)view.FindControl<Button>(buttonName)!.Command!;

    /// <summary>
    /// Shows the view in a window and waits for it to load: a view activates on its Loaded event, which
    /// is raised on a later dispatcher turn than the show.
    /// </summary>
    private async Task<(TView View, Window Window)> Show<TView>(Func<TView> create) where TView : Control
    {
        TView view = null!;
        Window window = null!;
        await OnUi(() =>
        {
            view = create();
            window = Track(new Window { Width = 800, Height = 600, Content = view });
            ShowRendered(window);
        });
        // Some overlay backgrounds never get their Loaded event from the headless render loop, so the
        // view is activated the way the loop would have.
        await OnUi(() =>
        {
            Render(window);
            if (!view.IsLoaded) view.RaiseEvent(new RoutedEventArgs(Control.LoadedEvent));
        });
        return (view, window);
    }

    [Fact]
    public async Task LaunchButtonView_ShowsTheLabelOfAViewModelThatArrivesAfterActivation()
    {
        var (view, _) = await Show(() => new LaunchButtonView());

        await OnUi(() => view.ViewModel = new LaunchButtonDesignViewModel { Label = "GO NOW" });

        await EventuallyOnUi(() => view.FindControl<TextBlock>("LaunchText")!.Text.Should().Be("GO NOW"));
    }

    [Theory]
    [InlineData("OkButton")]
    [InlineData("CloseButton")]
    public async Task MessageBoxOk_ButtonsWithoutAViewModel_DoNothing(string buttonName)
    {
        var (view, _) = await Show(() => new MessageBoxOkView { ViewModel = null }); // the XAML seeds a design view model

        await OnUi(async () =>
        {
            var act = async () => await CommandOf(view, buttonName).Execute().FirstOrDefaultAsync();
            await act.Should().NotThrowAsync();
        });
    }

    [Theory]
    [InlineData("OkButton", true)]
    [InlineData("CancelButton", false)]
    [InlineData("CloseButton", false)]
    public async Task MessageBoxOkCancel_ButtonsComplete_WithTheirAnswer(string buttonName, bool answer)
    {
        var viewModel = new FakeOkCancel();
        var (view, _) = await Show(() => new MessageBoxOkCancelView { ViewModel = viewModel });

        await OnUi(async () => await CommandOf(view, buttonName).Execute().FirstOrDefaultAsync());

        viewModel.Completed.Should().Equal(answer);
    }

    [Theory]
    [InlineData("OkButton")]
    [InlineData("CancelButton")]
    [InlineData("CloseButton")]
    public async Task MessageBoxOkCancel_ButtonsWithoutAViewModel_DoNothing(string buttonName)
    {
        var (view, _) = await Show(() => new MessageBoxOkCancelView());

        await OnUi(async () =>
        {
            var act = async () => await CommandOf(view, buttonName).Execute().FirstOrDefaultAsync();
            await act.Should().NotThrowAsync();
        });
    }

    [Theory]
    [InlineData("DeleteButton", true)]
    [InlineData("NoButton", false)]
    [InlineData("CloseButton", false)]
    public async Task LibraryItemDeleteConfirmation_ButtonsComplete_WithTheirAnswer(string buttonName, bool answer)
    {
        var viewModel = new FakeDeleteConfirmation();
        var (view, _) = await Show(() => new LibraryItemDeleteConfirmationView { ViewModel = viewModel });

        await OnUi(async () => await CommandOf(view, buttonName).Execute().FirstOrDefaultAsync());

        viewModel.Completed.Should().Equal(answer);
    }

    [Theory]
    [InlineData("DeleteButton")]
    [InlineData("NoButton")]
    [InlineData("CloseButton")]
    public async Task LibraryItemDeleteConfirmation_ButtonsWithoutAViewModel_DoNothing(string buttonName)
    {
        var (view, _) = await Show(() => new LibraryItemDeleteConfirmationView());

        await OnUi(async () =>
        {
            var act = async () => await CommandOf(view, buttonName).Execute().FirstOrDefaultAsync();
            await act.Should().NotThrowAsync();
        });
    }

    [Fact]
    public async Task MessageBoxOk_CopyDetailsWithoutAWindow_DoesNothing()
    {
        var (view, window) = await Show(() => new MessageBoxOkView { ViewModel = new MessageBoxOkDesignViewModel() });

        await OnUi(async () =>
        {
            var command = CommandOf(view, "CopyDetailsButton");
            window.Content = null;

            var act = async () => await command.Execute().FirstOrDefaultAsync();

            await act.Should().NotThrowAsync();
        });
    }

    [Fact]
    public async Task NexusLogin_CopyLinkWithoutAWindow_DoesNothing()
    {
        var (view, window) = await Show(() => new NexusLoginOverlayView { ViewModel = new FakeNexusLogin() });

        await OnUi(async () =>
        {
            var command = CommandOf(view, "CopyButton");
            window.Content = null;

            var act = async () => await command.Execute().FirstOrDefaultAsync();

            await act.Should().NotThrowAsync();
        });
    }
}
