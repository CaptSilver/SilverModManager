using System.Reactive;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using ReactiveUI;
using SilverModManager.App.UI.Controls.Cards;
using SilverModManager.App.UI.Pages.NexusCatalog;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.UI.Tests.Pages;

public class NexusModsViewTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class StubViewModel(IReadOnlyList<NexusModsModRow> mods, IThumbnailLoader thumbnails)
        : APageViewModel<INexusModsViewModel>(new DesignWindowManager()), INexusModsViewModel
    {
        private IReadOnlyList<NexusModsModRow> _mods = mods;

        public LoadoutId LoadoutId { get; set; }
        public string SearchText { get; set; } = "";
        public NexusModsSort Sort { get; set; } = NexusModsSort.Endorsements;

        public IReadOnlyList<NexusModsModRow> Mods
        {
            get => _mods;
            set => this.RaiseAndSetIfChanged(ref _mods, value);
        }

        public bool HasMore { get; set; }
        public bool IsLoading => false;
        public string ErrorText => "";
        public string InstallButtonText => "Install";
        public IThumbnailLoader Thumbnails { get; } = thumbnails;
        public INexusCollectionsViewModel Collections { get; } = new NexusCollectionsDesignViewModel();
        public int LoadMoreRequests;
        public ReactiveCommand<Unit, Unit> LoadMoreCommand { get; set; } = ReactiveCommand.Create(() => { });
        public ReactiveCommand<NexusModsModRow, Unit> InstallCommandOverride { get; set; } = ReactiveCommand.Create<NexusModsModRow>(_ => { });
        public ReactiveCommand<NexusModsModRow, Unit> InstallCommand => InstallCommandOverride;
        public ReactiveCommand<NexusModsModRow, Unit> OpenCommand { get; } = ReactiveCommand.Create<NexusModsModRow>(_ => { });
    }

    private sealed class FakeThumbnails : IThumbnailLoader
    {
        public IImage Placeholder { get; } = new RenderTargetBitmap(new PixelSize(2, 2));
        public Bitmap Loaded { get; } = new RenderTargetBitmap(new PixelSize(4, 4));
        public List<Uri> Requests { get; } = [];

        public IImage? TryGetCached(Uri url) => null;

        public Task<IImage> LoadAsync(Uri url, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(url);
            return Task.FromResult<IImage>(Loaded);
        }
    }

    private sealed class PendingThumbnails : IThumbnailLoader
    {
        public IImage Placeholder { get; } = new RenderTargetBitmap(new PixelSize(2, 2));
        public List<CancellationToken> Tokens { get; } = [];

        public IImage? TryGetCached(Uri url) => null;

        public async Task<IImage> LoadAsync(Uri url, CancellationToken cancellationToken)
        {
            lock (Tokens) Tokens.Add(cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("the delay only ends by cancellation");
        }
    }

    private static NexusModsModRow Mod(uint id, string name = "Some mod", string summary = "Does a thing", bool withThumbnail = true)
        => new(new NexusModsSearchHit(ModId.From(id), $"uid-{id}", name, summary, "author", "1.0", 1234, 1_500_000,
            new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
            withThumbnail ? new Uri($"https://img.example/{id}-t.png") : null, null, false));

    private static NexusModsModRow[] Mods(int count, int from = 0) => Enumerable.Range(from, count).Select(i => Mod((uint)i + 1, $"Mod {i}")).ToArray();

    private async Task<(Window Window, NexusModsView View)> Show(StubViewModel viewModel)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var view = new NexusModsView { ViewModel = viewModel };
            var window = new Window { Width = 900, Height = 700, Content = view };
            window.Show();
            return (window, view);
        });
    }

    private static string[] Texts(Control root) => root.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();

    private static Border[] Cards(Control root) => root.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("GridCard")).ToArray();

    [Fact]
    public async Task AMod_ShowsAsACardWithItsTextAndBothButtons()
    {
        var (window, view) = await Show(new StubViewModel([Mod(7, "Cyber Overhaul")], new FakeThumbnails()));

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            var card = Cards(view).Should().ContainSingle().Subject;
            var texts = Texts(card);
            texts.Should().Contain("Cyber Overhaul");
            texts.Should().Contain("by author");
            texts.Should().Contain(t => t.Contains("1.0") && t.Contains("1.2K") && t.Contains("1.5M") && t.Contains("2 Jan 2026"));
            texts.Should().Contain("Does a thing");
            card.GetVisualDescendants().OfType<Button>().Select(b => b.Content).Should().Equal("Install", "Open on Nexus Mods");
        });

        await OnUi(window.Close);
    }

    [Fact]
    public async Task TheThumbnail_FillsTheCardWidthInA16By9Frame()
    {
        var thumbnails = new FakeThumbnails();
        var (window, view) = await Show(new StubViewModel([Mod(7)], thumbnails));

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            var thumbnail = view.GetVisualDescendants().OfType<CardThumbnail>().Should().ContainSingle().Subject;
            thumbnail.Source.Should().BeSameAs(thumbnails.Loaded);
            thumbnail.Stretch.Should().Be(Stretch.UniformToFill);
            thumbnail.Width.Should().BeGreaterThan(190);
            (thumbnail.Width / thumbnail.Height).Should().BeApproximately(16.0 / 9, 0.02);
            var card = Cards(view).Single();
            thumbnail.Width.Should().BeLessThanOrEqualTo(card.Bounds.Width - card.Padding.Left - card.Padding.Right);
        });
        thumbnails.Requests.Should().Equal(new Uri("https://img.example/7-t.png"));

        await OnUi(window.Close);
    }

    [Fact]
    public async Task AModWithoutAThumbnail_ShowsThePlaceholderAndLoadsNothing()
    {
        var thumbnails = new FakeThumbnails();
        var (window, view) = await Show(new StubViewModel([Mod(7, withThumbnail: false)], thumbnails));

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            view.GetVisualDescendants().OfType<CardThumbnail>().Should().ContainSingle().Which.Source.Should().BeSameAs(thumbnails.Placeholder);
        });
        thumbnails.Requests.Should().BeEmpty();

        await OnUi(window.Close);
    }

    [Fact]
    public async Task AFullCard_KeepsEverythingInsideItsBorderAndClampsTheSummary()
    {
        var summary = string.Join(' ', Enumerable.Repeat("Replaces every texture in the game with something nicer", 8));
        var (window, view) = await Show(new StubViewModel([Mod(7, "An Extremely Long Mod Name That Wraps Onto Several Lines Of Text Here", summary)], new FakeThumbnails()));

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
            ToolTip.GetTip(text).Should().Be(summary);
            var install = card.GetVisualDescendants().OfType<Button>().First();
            (text.TranslatePoint(default, card)!.Value.Y + text.Bounds.Height).Should().BeLessThanOrEqualTo(install.TranslatePoint(default, card)!.Value.Y);
        });

        await OnUi(window.Close);
    }

    [Fact]
    public async Task ALongList_OnlyRealizesAndLoadsTheCardsOnScreen()
    {
        var thumbnails = new FakeThumbnails();
        var (window, view) = await Show(new StubViewModel(Mods(5000), thumbnails));

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            thumbnails.Requests.Should().NotBeEmpty();
        });

        var cards = Cards(view).Length;
        cards.Should().BeInRange(1, 60);
        view.GetVisualDescendants().OfType<CardThumbnail>().Count().Should().Be(cards);
        thumbnails.Requests.Count.Should().BeLessThan(100);
        await OnUi(window.Close);
    }

    private static ScrollViewer ScrollerOf(Control root) => root.GetVisualDescendants().OfType<CardGrid>().Single().GetVisualDescendants().OfType<ScrollViewer>().First();

    [Fact]
    public async Task ScrollingNearTheEnd_AsksForTheNextPage_ButNotBefore()
    {
        var vm = new StubViewModel(Mods(200), new FakeThumbnails()) { HasMore = true };
        vm.LoadMoreCommand = ReactiveCommand.Create(() => { vm.LoadMoreRequests++; });
        var (window, view) = await Show(vm);

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            Cards(view).Should().NotBeEmpty();
        });
        vm.LoadMoreRequests.Should().Be(0, "the end of 200 mods is many screens away");

        await OnUi(() =>
        {
            var scroller = ScrollerOf(view);
            scroller.Offset = new Vector(0, scroller.Extent.Height - scroller.Viewport.Height);
        });

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            vm.LoadMoreRequests.Should().BeGreaterThan(0);
        });
        await OnUi(window.Close);
    }

    [Fact]
    public async Task APageThatDoesNotFillTheView_AsksForTheNextOne()
    {
        var vm = new StubViewModel(Mods(2), new FakeThumbnails()) { HasMore = true };
        vm.LoadMoreCommand = ReactiveCommand.Create(() => { vm.LoadMoreRequests++; });
        var (window, _) = await Show(vm);

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            vm.LoadMoreRequests.Should().BeGreaterThan(0);
        });
        await OnUi(window.Close);
    }

    [Fact]
    public async Task APageThatHasNoMore_IsNotAskedFor()
    {
        var vm = new StubViewModel(Mods(2), new FakeThumbnails()) { HasMore = false };
        vm.LoadMoreCommand = ReactiveCommand.Create(() => { vm.LoadMoreRequests++; });
        var (window, view) = await Show(vm);

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            Cards(view).Should().HaveCount(2);
        });
        vm.LoadMoreRequests.Should().Be(0);
        await OnUi(window.Close);
    }

    [Fact]
    public async Task AppendingAPage_KeepsTheScrollPositionAndAddsTheCards()
    {
        var vm = new StubViewModel(Mods(200), new FakeThumbnails());
        var (window, view) = await Show(vm);

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            Cards(view).Should().NotBeEmpty();
        });
        double before = 0;
        await OnUi(() =>
        {
            var scroller = ScrollerOf(view);
            scroller.Offset = new Vector(0, scroller.Extent.Height - scroller.Viewport.Height);
        });
        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            before = ScrollerOf(view).Offset.Y;
            before.Should().BeGreaterThan(1000);
        });
        var extentBefore = ScrollerOf(view).Extent.Height;

        await OnUi(() => vm.Mods = vm.Mods.Concat(Mods(40, from: 200)).ToArray());

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            ScrollerOf(view).Extent.Height.Should().BeGreaterThan(extentBefore);
            ScrollerOf(view).Offset.Y.Should().BeGreaterThanOrEqualTo(before - 1, "a loaded page must not throw the user back to the top");
        });
        await OnUi(window.Close);
    }

    [Fact]
    public async Task ANewSearch_StartsBackAtTheTop()
    {
        var vm = new StubViewModel(Mods(200), new FakeThumbnails());
        var (window, view) = await Show(vm);

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            Cards(view).Should().NotBeEmpty();
        });
        await OnUi(() =>
        {
            var scroller = ScrollerOf(view);
            scroller.Offset = new Vector(0, 2000);
        });
        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            ScrollerOf(view).Offset.Y.Should().BeGreaterThan(1000);
        });

        await OnUi(() => vm.Mods = Mods(50, from: 1000));

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            ScrollerOf(view).Offset.Y.Should().Be(0);
            Texts(view).Should().Contain("Mod 1000");
        });
        await OnUi(window.Close);
    }

    [Fact]
    public async Task Install_GoesThroughTheCommandsCanExecute()
    {
        var executions = 0;
        var gate = new TaskCompletionSource();
        var vm = new StubViewModel([Mod(7)], new FakeThumbnails());
        vm.InstallCommandOverride = ReactiveCommand.CreateFromTask<NexusModsModRow>(async _ =>
        {
            Interlocked.Increment(ref executions);
            await gate.Task;
        });
        var (window, view) = await Show(vm);

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            view.GetVisualDescendants().OfType<Button>().Should().Contain(b => (string?)b.Content == "Install");
        });
        await OnUi(() =>
        {
            var button = view.GetVisualDescendants().OfType<Button>().First(b => (string?)b.Content == "Install");
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        });
        gate.SetResult();

        executions.Should().Be(1);
        await OnUi(window.Close);
    }

    [Fact]
    public async Task ACardLeavingTheScreen_CancelsItsThumbnailLoad()
    {
        var thumbnails = new PendingThumbnails();
        var (window, _) = await Show(new StubViewModel([Mod(7)], thumbnails));

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            thumbnails.Tokens.Should().ContainSingle();
        });
        thumbnails.Tokens[0].IsCancellationRequested.Should().BeFalse();

        await OnUi(() => window.Content = null);

        thumbnails.Tokens[0].IsCancellationRequested.Should().BeTrue();
        await OnUi(window.Close);
    }

    [Fact]
    public async Task EachTab_ShowsTheSharedSortBoxWithItsOwnSorts()
    {
        var (window, view) = await Show(new StubViewModel([], new FakeThumbnails()));

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            var box = view.GetVisualDescendants().OfType<StoreSortBox>().Should().ContainSingle().Subject;
            box.Items.OfType<StoreSortOption>().Select(o => o.Label).Should().Equal("Most endorsed", "Most downloaded", "Recently updated", "Name");
            box.SelectedKey.Should().Be("Endorsements");
        });

        // A tab that isn't selected has no controls yet, so the Collections tab is opened to see its box.
        await OnUi(() => view.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = 1);

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            var box = view.GetVisualDescendants().OfType<StoreSortBox>().Should().ContainSingle().Subject;
            box.Items.OfType<StoreSortOption>().Select(o => o.Label).Should().Equal("Most endorsed", "Most downloaded", "Recently updated");
        });

        await OnUi(window.Close);
    }
}
