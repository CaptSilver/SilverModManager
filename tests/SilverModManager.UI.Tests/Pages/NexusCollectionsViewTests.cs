using System.Reactive;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using ReactiveUI;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.App.UI.Controls.Cards;
using SilverModManager.App.UI.Pages.NexusCatalog;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.UI.Tests.Pages;

public class NexusCollectionsViewTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class StubCollections : ReactiveObject, INexusCollectionsViewModel
    {
        private IReadOnlyList<NexusCollectionRow> _collections = [];
        private bool _isVisible;

        public string SearchText { get; set; } = "";
        public NexusCollectionsSort Sort { get; set; } = NexusCollectionsSort.Endorsements;

        public IReadOnlyList<NexusCollectionRow> Collections
        {
            get => _collections;
            set => this.RaiseAndSetIfChanged(ref _collections, value);
        }

        public bool HasMore => false;
        public bool IsLoading => false;
        public string ErrorText => "";

        public bool IsVisible
        {
            get => _isVisible;
            set => this.RaiseAndSetIfChanged(ref _isVisible, value);
        }

        public IThumbnailLoader Thumbnails { get; } = new FakeThumbnails();
        public ReactiveCommand<Unit, Unit> LoadMoreCommand { get; } = ReactiveCommand.Create(() => { });
        public ReactiveCommand<NexusCollectionRow, Unit> PrimaryCommand { get; set; } = ReactiveCommand.Create<NexusCollectionRow>(_ => { });
        public ReactiveCommand<NexusCollectionRow, Unit> OpenOnNexusCommand { get; } = ReactiveCommand.Create<NexusCollectionRow>(_ => { });
    }

    private sealed class StubViewModel(INexusCollectionsViewModel collections, IReadOnlyList<NexusModsModRow> mods)
        : APageViewModel<INexusModsViewModel>(new DesignWindowManager()), INexusModsViewModel
    {
        public LoadoutId LoadoutId { get; set; }
        public string SearchText { get; set; } = "";
        public NexusModsSort Sort { get; set; } = NexusModsSort.Endorsements;
        public IReadOnlyList<NexusModsModRow> Mods { get; } = mods;
        public bool HasMore => false;
        public bool IsLoading => false;
        public string ErrorText => "";
        public string InstallButtonText => "Install";
        public IThumbnailLoader Thumbnails { get; } = new FakeThumbnails();
        public INexusCollectionsViewModel Collections { get; } = collections;
        public ReactiveCommand<Unit, Unit> LoadMoreCommand { get; } = ReactiveCommand.Create(() => { });
        public ReactiveCommand<NexusModsModRow, Unit> InstallCommand { get; } = ReactiveCommand.Create<NexusModsModRow>(_ => { });
        public ReactiveCommand<NexusModsModRow, Unit> OpenCommand { get; } = ReactiveCommand.Create<NexusModsModRow>(_ => { });
    }

    private sealed class FakeThumbnails : IThumbnailLoader
    {
        public IImage Placeholder { get; } = new RenderTargetBitmap(new PixelSize(2, 2));
        public Bitmap Loaded { get; } = new RenderTargetBitmap(new PixelSize(4, 4));

        public IImage? TryGetCached(Uri url) => null;
        public Task<IImage> LoadAsync(Uri url, CancellationToken cancellationToken) => Task.FromResult<IImage>(Loaded);
    }

    private static NexusCollectionRow Collection(int id, string name = "Some collection", string summary = "Does a thing", bool adult = false, bool inLibrary = false)
        => new(new NexusCollectionSearchHit(CollectionSlug.From($"slug{id}"), GameDomain.From("cyberpunk2077"), name, summary, "curator", 12, 3400, 45, 123_456_789UL,
            RevisionNumber.From(7), new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), new Uri($"https://img.example/{id}.png"), adult),
            inLibrary ? default(CollectionRevisionMetadataId) : null);

    private static NexusCollectionRow[] Collections(int count) => Enumerable.Range(0, count).Select(i => Collection(i + 1, $"Collection {i}")).ToArray();

    private static NexusModsModRow Mod(uint id)
        => new(new NexusModsSearchHit(ModId.From(id), $"uid-{id}", $"Mod {id}", "A mod", "author", "1.0", 1, 1,
            new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), null, null, false));

    private async Task<(Window Window, NexusModsView View)> Show(StubViewModel viewModel)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var view = new NexusModsView { ViewModel = viewModel };
            var window = Track(new Window { Width = 900, Height = 700, Content = view });
            window.Show();
            return (window, view);
        });
    }

    private static TabControl TabsOf(Control view) => view.GetVisualDescendants().OfType<TabControl>().Single();

    private static async Task SelectCollections(NexusModsView view) => await Dispatcher.UIThread.InvokeAsync(() => TabsOf(view).SelectedIndex = 1);

    private static string[] Texts(Control root) => root.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();

    // A hidden control is still in the tree, so what the user can read is narrower than what Texts finds.
    private static string[] ShownTexts(Control root) => root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToArray();

    private static Border[] Cards(Control root) => root.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("GridCard")).ToArray();

    [Fact]
    public async Task ThePage_OpensOnTheModsTab_AndOffersACollectionsTab()
    {
        var (window, view) = await Show(new StubViewModel(new StubCollections(), [Mod(1)]));

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            var tabs = TabsOf(view);
            tabs.Items.OfType<TabItem>().Select(t => Texts((Control)t.Header!).Single()).Should().Equal("Mods", "Collections");
            tabs.SelectedIndex.Should().Be(0);
            Texts(view).Should().Contain("Mod 1");
        });

        await OnUi(window.Close);
    }

    [Fact]
    public async Task ACollection_ShowsAsACardWithItsTextAndBothButtons()
    {
        var collections = new StubCollections { Collections = [Collection(1, "Vanilla Plus")] };
        var (window, view) = await Show(new StubViewModel(collections, []));
        await SelectCollections(view);

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            var card = Cards(view).Should().ContainSingle().Subject;
            var texts = Texts(card);
            texts.Should().Contain("Vanilla Plus");
            texts.Should().Contain("by curator");
            texts.Should().Contain(t => t.Contains("45 mods") && t.Contains("118 MB") && t.Contains("12 endorsements") && t.Contains("3.4K downloads"));
            texts.Should().Contain("Does a thing");
            ShownTexts(card).Should().NotContain("Adult");
            card.GetVisualDescendants().OfType<Button>().Select(b => b.Content).Should().Equal("Install", "Open on Nexus Mods");
        });

        await OnUi(window.Close);
    }

    [Fact]
    public async Task AnAdultCollection_ShowsABadge()
    {
        var collections = new StubCollections { Collections = [Collection(1, adult: true)] };
        var (window, view) = await Show(new StubViewModel(collections, []));
        await SelectCollections(view);

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            ShownTexts(Cards(view).Should().ContainSingle().Subject).Should().Contain("Adult");
        });

        await OnUi(window.Close);
    }

    [Fact]
    public async Task ACollectionInTheLibrary_ShowsOpenInPlaceOfInstall_AndFlipsWhenItEnters()
    {
        var row = Collection(1);
        var collections = new StubCollections { Collections = [row, Collection(2, inLibrary: true)] };
        var (window, view) = await Show(new StubViewModel(collections, []));
        await SelectCollections(view);

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            Cards(view).Select(c => c.GetVisualDescendants().OfType<Button>().First().Content).Should().Equal("Install", "Open");
        });

        await OnUi(() => row.LibraryRevision = default(CollectionRevisionMetadataId));

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            Cards(view).Select(c => c.GetVisualDescendants().OfType<Button>().First().Content).Should().Equal("Open", "Open");
        });

        await OnUi(window.Close);
    }

    [Fact]
    public async Task AFullCard_KeepsEverythingInsideItsBorderAndClampsTheSummary()
    {
        var summary = string.Join(' ', Enumerable.Repeat("Replaces every texture in the game with something nicer", 8));
        var collections = new StubCollections { Collections = [Collection(1, "An Extremely Long Collection Name That Wraps Onto Several Lines Of Text Here", summary, adult: true)] };
        var (window, view) = await Show(new StubViewModel(collections, []));
        await SelectCollections(view);

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            var card = Cards(view).Should().ContainSingle().Subject;
            var inside = new Rect(card.Bounds.Size).Deflate(card.Padding);
            foreach (var control in card.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c is TextBlock or Button or CardThumbnail))
            {
                var bounds = new Rect(control.TranslatePoint(default, card)!.Value, control.Bounds.Size);
                inside.Contains(bounds).Should().BeTrue($"{control.GetType().Name} at {bounds} should sit inside {inside}");
            }

            var text = card.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == summary);
            text.MaxLines.Should().Be(3);
            var thumbnail = card.GetVisualDescendants().OfType<CardThumbnail>().Single();
            (thumbnail.Width / thumbnail.Height).Should().BeApproximately(16.0 / 9, 0.02);
        });

        await OnUi(window.Close);
    }

    [Fact]
    public async Task ALongList_OnlyRealizesTheCardsOnScreen()
    {
        var collections = new StubCollections { Collections = Collections(5000) };
        var (window, view) = await Show(new StubViewModel(collections, []));
        await SelectCollections(view);

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            Cards(view).Should().NotBeEmpty();
        });

        var cards = Cards(view).Length;
        cards.Should().BeInRange(1, 60);
        view.GetVisualDescendants().OfType<CardThumbnail>().Count().Should().Be(cards);
        await OnUi(window.Close);
    }

    [Fact]
    public async Task SwitchingTabs_TellsTheCollectionsViewModelWhetherItIsShown()
    {
        var collections = new StubCollections();
        var (window, view) = await Show(new StubViewModel(collections, [Mod(1)]));

        await EventuallyOnUi(() => collections.IsVisible.Should().BeFalse());

        await SelectCollections(view);
        await EventuallyOnUi(() => collections.IsVisible.Should().BeTrue());

        await OnUi(() => TabsOf(view).SelectedIndex = 0);
        await EventuallyOnUi(() => collections.IsVisible.Should().BeFalse());

        await OnUi(window.Close);
    }

    [Fact]
    public async Task EachTab_KeepsItsOwnSearchAndSort_AcrossSwitches()
    {
        var collections = new StubCollections { Collections = [Collection(1)] };
        var viewModel = new StubViewModel(collections, [Mod(1)]);
        var (window, view) = await Show(viewModel);
        await EventuallyOnUi(() => view.FindControl<TextBox>("SearchBox").Should().NotBeNull());

        await OnUi(() =>
        {
            view.FindControl<TextBox>("SearchBox")!.Text = "armor";
            view.FindControl<ComboBox>("SortBox")!.SelectedIndex = 1;
        });
        await SelectCollections(view);
        await EventuallyOnUi(() => view.FindControl<TextBox>("CollectionSearchBox").Should().NotBeNull());
        await OnUi(() =>
        {
            view.FindControl<TextBox>("CollectionSearchBox")!.Text.Should().BeNullOrEmpty("the collections search starts empty, whatever was typed for mods");
            view.FindControl<TextBox>("CollectionSearchBox")!.Text = "vanilla";
            view.FindControl<ComboBox>("CollectionSortBox")!.SelectedIndex = 2;
        });

        await OnUi(() => TabsOf(view).SelectedIndex = 0);
        await EventuallyOnUi(() =>
        {
            view.FindControl<TextBox>("SearchBox")!.Text.Should().Be("armor");
            view.FindControl<ComboBox>("SortBox")!.SelectedIndex.Should().Be(1);
        });
        await SelectCollections(view);

        await EventuallyOnUi(() =>
        {
            view.FindControl<TextBox>("CollectionSearchBox")!.Text.Should().Be("vanilla");
            view.FindControl<ComboBox>("CollectionSortBox")!.SelectedIndex.Should().Be(2);
        });
        collections.SearchText.Should().Be("vanilla");
        collections.Sort.Should().Be(NexusCollectionsSort.Updated);
        viewModel.SearchText.Should().Be("armor");
        viewModel.Sort.Should().Be(NexusModsSort.Downloads);

        await OnUi(window.Close);
    }

    [Fact]
    public async Task ThePrimaryButton_GoesThroughTheCommandsCanExecute()
    {
        var executions = 0;
        var gate = new TaskCompletionSource();
        var collections = new StubCollections { Collections = [Collection(1)] };
        collections.PrimaryCommand = ReactiveCommand.CreateFromTask<NexusCollectionRow>(async _ =>
        {
            Interlocked.Increment(ref executions);
            await gate.Task;
        });
        var (window, view) = await Show(new StubViewModel(collections, []));
        await SelectCollections(view);

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            view.GetVisualDescendants().OfType<Button>().Should().Contain(b => (string?)b.Content == "Install");
        });
        await OnUi(() =>
        {
            var button = view.GetVisualDescendants().OfType<Button>().First(b => (string?)b.Content == "Install");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        gate.SetResult();

        executions.Should().Be(1);
        await OnUi(window.Close);
    }
}
