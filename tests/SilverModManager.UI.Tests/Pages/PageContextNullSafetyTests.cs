using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using FluentAssertions;
using SilverModManager.App.UI.Pages.DebugControls;
using SilverModManager.App.UI.Pages.Diff.ApplyDiff;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.App.UI.Pages.LibraryPage.Collections;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.App.UI.Pages.MyLoadouts;
using SilverModManager.App.UI.Pages.TextEdit;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.App.UI.Windows;
using System.Collections.ObjectModel;
using System.Reactive;
using ReactiveUI;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.App.UI.Pages.MyLoadouts.GameLoadoutsSectionEntry;
using SilverModManager.App.UI.Controls;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// Page view models that hold something only once a window or a database hands it over, and the views that
/// observe them before that happens.
/// </summary>
public class PageContextNullSafetyTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public void RequireStorageProvider_WithoutOne_RefusesWithAReadableMessage()
    {
        var act = () => LibraryViewModel.RequireStorageProvider(null);

        act.Should().Throw<UserFacingException>().WithMessage("This window cannot open a file picker");
    }

    [Theory]
    [InlineData(typeof(IApplyDiffViewModel))]
    [InlineData(typeof(ApplyDiffViewModel))]
    [InlineData(typeof(ApplyDiffDesignViewModel))]
    public void BodyViewModel_IsNullableUntilTheDiffTreeIsBuilt(Type owner)
    {
        var property = owner.GetProperty(nameof(IApplyDiffViewModel.BodyViewModel), BindingFlags.Public | BindingFlags.Instance)!;

        new NullabilityInfoContext().Create(property).ReadState.Should().Be(NullabilityState.Nullable);
    }

    [Fact]
    public async Task CollectionLoadoutDesignViewModel_HasATreeToBind()
    {
        await OnUi(() => new CollectionLoadoutDesignViewModel().Adapter.Should().NotBeNull());
    }

    [Fact]
    public void DebugControlsDesignViewModel_HasAServiceProvider()
    {
        new DebugControlsPageDesignViewModel().ServiceProvider.Should().NotBeNull();
    }

    [Fact]
    public async Task DebugControlsView_OpeningTheMarkdownExample_WithoutAViewModel_DoesNothing()
    {
        await OnUi(() =>
        {
            var view = new DebugControlsPageView();
            var window = Track(new Window { Content = view });
            ShowRendered(window);

            var act = () => view.FindControl<Button>("ShowModalExampleMarkdown")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            act.Should().NotThrow();
        });
    }

    [Fact]
    public async Task CollectionCardView_ShowsAViewModelThatArrivesAfterActivation()
    {
        await OnUi(() =>
        {
            var view = new CollectionCardView();
            var window = Track(new Window { Content = view });
            ShowRendered(window);

            var viewModel = new CollectionCardDesignViewModel();
            view.ViewModel = viewModel;

            var title = view.FindControl<TextBlock>("TitleText")!;
            ToolTip.GetTip(title).Should().Be(viewModel.Name);
        });
    }

    [Fact]
    public async Task TextEditorView_KeepsPasteDisabledUntilTheViewModelAllowsEditing()
    {
        await OnUi(() =>
        {
            var view = new TextEditorPageView();
            var window = Track(new Window { Content = view });
            ShowRendered(window);
            var paste = view.FindControl<Button>("PasteButton")!.Command!;

            paste.CanExecute(null).Should().BeFalse("no view model has said the editor is writable");

            view.ViewModel = Provider.GetRequiredService<ITextEditorPageViewModel>();
            Render(window);

            view.ViewModel.IsReadOnly.Should().BeTrue();
            paste.CanExecute(null).Should().BeFalse("the view model starts read-only");
        });
    }

    [Fact]
    public async Task MyLoadoutsView_StopsTrackingTheEmptyStateOnceDeactivated()
    {
        await OnUi(() =>
        {
            var viewModel = new SectionsViewModel();
            var view = new MyLoadoutsView { ViewModel = viewModel };
            var window = Track(new Window { Content = view });
            ShowRendered(window);
            var emptyState = view.FindControl<EmptyState>("MyLoadoutsEmptyState")!;
            emptyState.IsActive.Should().BeTrue("there are no sections yet");

            window.Content = null;
            Render(window);
            viewModel.Sections.Add(new GameLoadoutsSectionEntryDesignViewModel());
            Render(window);

            emptyState.IsActive.Should().BeTrue("the deactivated view no longer follows the sections");
        });
    }

    private sealed class SectionsViewModel : APageViewModel<IMyLoadoutsViewModel>, IMyLoadoutsViewModel
    {
        public ObservableCollection<IGameLoadoutsSectionEntryViewModel> Sections { get; } = [];

        public SectionsViewModel() : base(new DesignWindowManager())
        {
            GameSectionViewModels = new ReadOnlyObservableCollection<IGameLoadoutsSectionEntryViewModel>(Sections);
        }

        public ReadOnlyObservableCollection<IGameLoadoutsSectionEntryViewModel> GameSectionViewModels { get; }
        public bool IsFilteredToGame => false;
        public string FilterHeadingText => "";
        public ReactiveCommand<Unit, Unit> ShowAllGamesCommand { get; } = ReactiveCommand.Create(() => { });
    }
}
