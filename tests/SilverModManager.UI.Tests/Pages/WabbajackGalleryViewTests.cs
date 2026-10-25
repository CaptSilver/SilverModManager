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
using SilverModManager.App.UI.Pages.WabbajackGallery;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Gallery;
using static SilverModManager.UI.Tests.Pages.WabbajackGalleryViewModelTests;

namespace SilverModManager.UI.Tests.Pages;

public class WabbajackGalleryViewTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class StubViewModel : APageViewModel<IWabbajackGalleryViewModel>, IWabbajackGalleryViewModel
    {
        public StubViewModel(IReadOnlyList<WabbajackGalleryRow> lists, IThumbnailLoader thumbnails) : base(new DesignWindowManager())
        {
            Lists = lists;
            Thumbnails = thumbnails;
            OpenLinkCommand = ReactiveCommand.Create<Uri>(Opened.Add);
        }

        public LoadoutId LoadoutId { get; set; }
        public string SearchText { get; set; } = "";
        public WabbajackGallerySort Sort { get; set; }
        public IReadOnlyList<WabbajackGalleryRow> Lists { get; }
        public IThumbnailLoader Thumbnails { get; }
        public string ErrorText => "";
        public string RefreshedText => "Last refreshed 1 Oct 12:00";
        public bool IsRefreshing => false;
        public List<Uri> Opened { get; } = [];
        public ReactiveCommand<Unit, Unit> RefreshCommand { get; } = ReactiveCommand.Create(() => { });
        public ReactiveCommand<WabbajackGalleryRow, Unit> InstallCommand { get; } = ReactiveCommand.Create<WabbajackGalleryRow>(_ => { });
        public ReactiveCommand<Uri, Unit> OpenLinkCommand { get; }
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

    private static WabbajackGalleryRow Row(string title, Action<GalleryEntryBuilder>? configure = null, LibraryFileId? inLibrary = null)
        => new(Entry(title, "Mara", configure: b =>
        {
            b.Official = true;
            b.Tags = ["Quests", "Survival"];
            b.Archives = 85UL * 1024 * 1024 * 1024;
            b.Installed = 210UL * 1024 * 1024 * 1024;
            configure?.Invoke(b);
        }) with
        {
            Image = new Uri($"https://example.invalid/{title}.webp"),
            Readme = new Uri("https://example.invalid/readme"),
            Website = new Uri("https://example.invalid/site"),
            Description = "A long and winding description",
        }, inLibrary);

    private async Task<(Window Window, WabbajackGalleryView View)> Show(StubViewModel viewModel)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var view = new WabbajackGalleryView { ViewModel = viewModel };
            var window = new Window { Width = 1000, Height = 800, Content = view };
            window.Show();
            return (window, view);
        });
    }

    private static string[] Texts(Control root) => root.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();

    private static Border[] Cards(Control root) => root.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("GridCard")).ToArray();

    [Fact]
    public async Task ACard_ShowsTheImageTitleAuthorVersionSizesTagsAndOfficialMark()
    {
        var thumbnails = new FakeThumbnails();
        var (window, view) = await Show(new StubViewModel([Row("Nightfall")], thumbnails));

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            var card = Cards(view).Should().ContainSingle().Subject;
            var texts = Texts(card);
            texts.Should().Contain("Nightfall");
            texts.Should().Contain("by Mara");
            texts.Should().Contain(t => t.Contains("1.0"));
            texts.Should().Contain("download 85 GB · installed 210 GB");
            texts.Should().Contain("Quests").And.Contain("Survival");
            texts.Should().Contain("Official");

            var image = card.GetVisualDescendants().OfType<CardThumbnail>().Should().ContainSingle().Subject;
            image.Source.Should().BeSameAs(thumbnails.Loaded);
            // The gallery's pictures are wide: a 16:9 frame like the Nexus cards.
            (image.Width / image.Height).Should().BeApproximately(16.0 / 9.0, 0.1);
        });
        thumbnails.Requests.Should().Equal(new Uri("https://example.invalid/Nightfall.webp"));

        await OnUi(window.Close);
    }

    [Fact]
    public async Task ACard_ClampsTheDescriptionToThreeLines()
    {
        var (window, view) = await Show(new StubViewModel([Row("Nightfall")], new FakeThumbnails()));

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            Cards(view).Single().GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "A long and winding description").MaxLines.Should().Be(3);
        });

        await OnUi(window.Close);
    }

    [Fact]
    public async Task ACard_ButtonsReadInstallReadmeAndWebsite_AndOpenWhenTheListIsInTheLibrary()
    {
        var inLibrary = LibraryFileId.From(EntityId.From(0xABC));
        var (window, view) = await Show(new StubViewModel([Row("Want"), Row("Have", inLibrary: inLibrary)], new FakeThumbnails()));

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            var cards = Cards(view);
            cards.Should().HaveCount(2);
            foreach (var card in cards)
                card.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => (string?)b.Content)
                    .Should().Contain(["Readme", "Website"]);

            ButtonsOf(cards, "Want").Should().Contain("Install").And.NotContain("Open");
            ButtonsOf(cards, "Have").Should().Contain("Open").And.NotContain("Install");
        });

        await OnUi(window.Close);

        static IEnumerable<string?> ButtonsOf(Border[] cards, string title)
            => cards.Single(c => Texts(c).Contains(title)).GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => (string?)b.Content);
    }

    [Fact]
    public async Task ACardWithoutReadmeOrWebsite_ShowsNeitherButton()
    {
        var bare = Row("Bare") with { Entry = Row("Bare").Entry with { Readme = null, Website = null } };
        var (window, view) = await Show(new StubViewModel([bare], new FakeThumbnails()));

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            Cards(view).Single().GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => (string?)b.Content)
                .Should().Equal("Install");
        });

        await OnUi(window.Close);
    }

    [Fact]
    public async Task ClickingReadmeAndWebsite_OpensTheirAddresses()
    {
        var viewModel = new StubViewModel([Row("Nightfall")], new FakeThumbnails());
        var (window, view) = await Show(viewModel);

        await EventuallyOnUi(() => Dispatcher.UIThread.RunJobs());
        await OnUi(() =>
        {
            foreach (var label in new[] { "Readme", "Website" })
                view.GetVisualDescendants().OfType<Button>().First(b => (string?)b.Content == label)
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        });

        await EventuallyOnUi(() => viewModel.Opened.Should().Equal(new Uri("https://example.invalid/readme"), new Uri("https://example.invalid/site")));
        await OnUi(window.Close);
    }

    [Fact]
    public async Task AFullCard_KeepsEverythingInsideItsBorder()
    {
        var long_ = string.Join(' ', Enumerable.Repeat("A description that goes on and on", 12));
        var row = Row("An_Extremely_Long_Modlist_Title_That_Wraps_Onto_Several_Lines_Of_Text_And_More") with
        {
            Entry = Row("x").Entry with
            {
                Title = "An Extremely Long Modlist Title That Wraps Onto Several Lines Of Text And More",
                Description = long_,
                Author = "SomeVeryLongAuthorNameThatKeepsGoing",
                Tags = ["Quests", "Survival", "Gameplay", "Combat", "Magic", "Visuals"],
            },
        };
        var (window, view) = await Show(new StubViewModel([row], new FakeThumbnails()));

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            var card = Cards(view).Single();
            var inside = new Rect(card.Bounds.Size).Deflate(card.Padding);
            foreach (var control in card.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c is TextBlock or Button or CardThumbnail))
            {
                var bounds = new Rect(control.TranslatePoint(default, card)!.Value, control.Bounds.Size);
                inside.Contains(bounds).Should().BeTrue($"{control.GetType().Name} at {bounds} should sit inside {inside}");
            }

            // The description ends above the buttons rather than running under them.
            var description = card.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == long_);
            var firstButton = card.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible);
            (description.TranslatePoint(default, card)!.Value.Y + description.Bounds.Height)
                .Should().BeLessThanOrEqualTo(firstButton.TranslatePoint(default, card)!.Value.Y);
        });

        await OnUi(window.Close);
    }

    [Fact]
    public async Task ALongList_RealizesOnlyAScreenfulOfCardsAndLoadsOnlyTheirImages()
    {
        var thumbnails = new FakeThumbnails();
        var rows = Enumerable.Range(0, 5000).Select(i => Row($"List {i}")).ToArray();
        var (window, view) = await Show(new StubViewModel(rows, thumbnails));

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

    [Fact]
    public async Task TheSortBox_OffersOnlySortsTheGalleryCanAnswer()
    {
        var (window, view) = await Show(new StubViewModel([Row("A")], new FakeThumbnails()) { Sort = WabbajackGallerySort.Name });

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            var box = view.GetVisualDescendants().OfType<StoreSortBox>().Should().ContainSingle().Subject;
            box.Items.OfType<StoreSortOption>().Select(o => o.Label)
                .Should().Equal("Recently updated", "Newest", "Name", "Largest download", "Smallest download");
            box.SelectedKey.Should().Be("Name");
        });

        await OnUi(window.Close);
    }

    [Fact]
    public async Task ThePage_ShowsWhenTheGalleryWasFetchedAndARefreshButton()
    {
        var (window, view) = await Show(new StubViewModel([Row("A")], new FakeThumbnails()));

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            Texts(view).Should().Contain("Last refreshed 1 Oct 12:00");
            view.GetVisualDescendants().OfType<Control>().Should().Contain(c => c.Name == "RefreshButton");
        });

        await OnUi(window.Close);
    }
}
