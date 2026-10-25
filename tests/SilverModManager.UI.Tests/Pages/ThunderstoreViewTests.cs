using SilverModManager.App.UI.Controls.Cards;
using System.Reactive;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using ReactiveUI;
using SilverModManager.App.UI.Pages.Thunderstore;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.UI.Tests.Pages;

public class ThunderstoreViewTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class StubViewModel(IReadOnlyList<ThunderstorePackageRow> packages, IThumbnailLoader icons)
        : APageViewModel<IThunderstoreViewModel>(new DesignWindowManager()), IThunderstoreViewModel
    {
        public LoadoutId LoadoutId { get; set; }
        public string SearchText { get; set; } = "";
        public bool ShowDeprecated { get; set; }
        public ThunderstoreSort Sort { get; set; }
        public IReadOnlyList<ThunderstorePackageRow> Packages { get; } = packages;
        public IThumbnailLoader Icons { get; } = icons;
        public string ErrorText => "";
        public bool IsRefreshing => false;
        public ReactiveCommand<Unit, Unit> RefreshCommand { get; } = ReactiveCommand.Create(() => { });
        public ReactiveCommand<ThunderstorePackageRow, Unit> OpenPackageCommand { get; } = ReactiveCommand.Create<ThunderstorePackageRow>(_ => { });
        public ReactiveCommand<ThunderstorePackageRow, Unit> InstallCommandOverride { get; set; } = ReactiveCommand.Create<ThunderstorePackageRow>(_ => { });
        public ReactiveCommand<ThunderstorePackageRow, Unit> InstallCommand => InstallCommandOverride;
    }

    private sealed class FakeIcons : IThumbnailLoader
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

    private static ThunderstorePackageRow Row(string owner, string name, string? icon = "https://example.com/icon.png")
        => new($"{owner}-{name}", "4.0.2", 112, false, "Lifts the cap", new Uri("https://thunderstore.io/c/peak/p/x/y/"),
            Owner: owner, DisplayName: ThunderstoreRowText.DisplayName(name, owner), IconUrl: icon is null ? null : new Uri(icon), Downloads: 1_234_567);

    private async Task<(Window Window, ThunderstoreView View)> Show(StubViewModel viewModel)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var view = new ThunderstoreView { ViewModel = viewModel };
            var window = new Window { Width = 900, Height = 700, Content = view };
            window.Show();
            return (window, view);
        });
    }

    private static string[] Texts(Control root) => root.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();

    [Fact]
    public async Task ARow_ShowsTheReadableNameTheOwnerAndAnIcon()
    {
        var icons = new FakeIcons();
        var (window, view) = await Show(new StubViewModel([Row("glarmer", "PEAK_Unlimited")], icons));

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            var texts = Texts(view);
            texts.Should().Contain("PEAK Unlimited");
            texts.Should().Contain("by glarmer");
            texts.Should().NotContain(t => t.Contains("glarmer-PEAK_Unlimited"));
            texts.Should().Contain(t => t.Contains("4.0.2") && t.Contains("1.2M"));

            var icon = view.GetVisualDescendants().OfType<CardThumbnail>().Should().ContainSingle().Subject;
            icon.Source.Should().BeSameAs(icons.Loaded);
            icon.Width.Should().BeGreaterThan(150);
            icon.Width.Should().Be(icon.Height);
        });

        await OnUi(window.Close);
    }

    [Fact]
    public async Task ARowWithoutAnIcon_ShowsThePlaceholderAndLoadsNothing()
    {
        var icons = new FakeIcons();
        var (window, view) = await Show(new StubViewModel([Row("Zehs", "REPOLib", icon: null)], icons));

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            view.GetVisualDescendants().OfType<CardThumbnail>().Should().ContainSingle().Which.Source.Should().BeSameAs(icons.Placeholder);
        });
        icons.Requests.Should().BeEmpty();

        await OnUi(window.Close);
    }

    [Fact]
    public async Task ALongList_OnlyLoadsTheIconsOfTheRowsOnScreen()
    {
        var icons = new FakeIcons();
        var rows = Enumerable.Range(0, 5000).Select(i => Row("owner", $"Package_{i}", icon: $"https://example.com/{i}.png")).ToArray();
        var (window, view) = await Show(new StubViewModel(rows, icons));

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            icons.Requests.Should().NotBeEmpty();
        });

        icons.Requests.Count.Should().BeLessThan(100);
        await OnUi(window.Close);
    }

    private async Task<(Window Window, ThunderstoreView View)> Show(StubViewModel viewModel, double width)
    {
        var (window, view) = await Show(viewModel);
        await OnUi(() => window.Width = width);
        return (window, view);
    }

    private static int Columns(Control root)
    {
        Dispatcher.UIThread.RunJobs();
        return root.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("GridCard"))
            .Select(b => Math.Round(b.TranslatePoint(default, root)!.Value.X)).Distinct().Count();
    }

    [Fact]
    public async Task TheGrid_ReflowsWhenTheWindowIsResized()
    {
        var rows = Enumerable.Range(0, 40).Select(i => Row("owner", $"Package_{i}")).ToArray();
        var (window, view) = await Show(new StubViewModel(rows, new FakeIcons()), 1300);

        int wide = 0, narrow = 0;
        await EventuallyOnUi(() =>
        {
            wide = Columns(view);
            wide.Should().BeGreaterThan(3);
        });
        await OnUi(() => window.Width = 560);
        await EventuallyOnUi(() =>
        {
            narrow = Columns(view);
            narrow.Should().BeLessThan(wide);
            narrow.Should().BeGreaterThan(0);
        });

        await OnUi(window.Close);
    }

    [Fact]
    public async Task ALongList_RealizesOnlyAScreenfulOfCards()
    {
        var icons = new FakeIcons();
        var rows = Enumerable.Range(0, 5000).Select(i => Row("owner", $"Package_{i}", icon: $"https://example.com/{i}.png")).ToArray();
        var (window, view) = await Show(new StubViewModel(rows, icons));

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            icons.Requests.Should().NotBeEmpty();
        });

        var cards = view.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("GridCard"));
        cards.Should().BeInRange(1, 60);
        view.GetVisualDescendants().OfType<CardThumbnail>().Count().Should().Be(cards);
        await OnUi(window.Close);
    }

    [Fact]
    public async Task ACard_ShowsTitleOwnerStatsAndBothButtons()
    {
        var latest = new SilverModManager.Sdk.Thunderstore.ThunderstoreVersionRef(new("glarmer", "PEAK_Unlimited"), new(4, 0, 2));
        var row = Row("glarmer", "PEAK_Unlimited") with { Latest = latest };
        var (window, view) = await Show(new StubViewModel([row], new FakeIcons()));

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            var card = view.GetVisualDescendants().OfType<Border>().Should().ContainSingle(b => b.Classes.Contains("GridCard")).Subject;
            var texts = Texts(card);
            texts.Should().Contain("PEAK Unlimited");
            texts.Should().Contain("by glarmer");
            texts.Should().Contain(t => t.Contains("4.0.2") && t.Contains("1.2M"));
            card.GetVisualDescendants().OfType<Button>().Select(b => b.Content).Should().Equal("Install", "Open on thunderstore.io");
        });

        await OnUi(window.Close);
    }

    [Fact]
    public async Task AFullCard_KeepsEverythingInsideItsBorder()
    {
        var latest = new SilverModManager.Sdk.Thunderstore.ThunderstoreVersionRef(new("SomeVeryLongOwnerName", "X"), new(4, 0, 2));
        var longText = string.Join(' ', Enumerable.Repeat("Lifts the cap on the number of players in a lobby", 6));
        var row = Row("SomeVeryLongOwnerName", "An_Extremely_Long_Package_Name_That_Wraps_Onto_Several_Lines_Of_Text")
            with { Latest = latest, IsDeprecated = true, Description = longText };
        var (window, view) = await Show(new StubViewModel([row], new FakeIcons()));

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            var card = view.GetVisualDescendants().OfType<Border>().Should().ContainSingle(b => b.Classes.Contains("GridCard")).Subject;
            card.GetVisualDescendants().OfType<Button>().Should().HaveCount(2);
            var inside = new Rect(card.Bounds.Size).Deflate(card.Padding);
            foreach (var control in card.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c is TextBlock or Button or CardThumbnail))
            {
                var bounds = new Rect(control.TranslatePoint(default, card)!.Value, control.Bounds.Size);
                inside.Contains(bounds).Should().BeTrue($"{control.GetType().Name} at {bounds} should sit inside {inside}");
            }

            // The description has to end above the buttons, not run underneath them.
            var description = card.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == longText);
            var install = card.GetVisualDescendants().OfType<Button>().First();
            (description.TranslatePoint(default, card)!.Value.Y + description.Bounds.Height)
                .Should().BeLessThanOrEqualTo(install.TranslatePoint(default, card)!.Value.Y);
        });

        await OnUi(window.Close);
    }

    [Fact]
    public async Task Install_GoesThroughTheCommandsCanExecute()
    {
        var latest = new SilverModManager.Sdk.Thunderstore.ThunderstoreVersionRef(new("glarmer", "PEAK_Unlimited"), new(4, 0, 2));
        var row = Row("glarmer", "PEAK_Unlimited") with { Latest = latest };
        var executions = 0;
        var gate = new TaskCompletionSource();
        var vm = new StubViewModel([row], new FakeIcons());
        var install = ReactiveCommand.CreateFromTask<ThunderstorePackageRow>(async _ =>
        {
            Interlocked.Increment(ref executions);
            await gate.Task;
        });
        vm.InstallCommandOverride = install;
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

    private sealed class PendingIcons : IThumbnailLoader
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

    [Fact]
    public async Task ARowLeavingTheScreen_CancelsItsIconLoad()
    {
        var icons = new PendingIcons();
        var (window, _) = await Show(new StubViewModel([Row("glarmer", "PEAK_Unlimited")], icons));

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            icons.Tokens.Should().ContainSingle();
        });
        icons.Tokens[0].IsCancellationRequested.Should().BeFalse();

        // Detaching is what a virtualized list does to a row that scrolls out of view.
        await OnUi(() => window.Content = null);

        icons.Tokens[0].IsCancellationRequested.Should().BeTrue();
        await OnUi(window.Close);
    }

    [Fact]
    public async Task TheSortBox_ListsTheSharedWordingAndShowsTheViewModelsSort()
    {
        var (window, view) = await Show(new StubViewModel([Row("a", "B")], new FakeIcons()) { Sort = ThunderstoreSort.Newest });

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            var box = view.GetVisualDescendants().OfType<StoreSortBox>().Should().ContainSingle().Subject;
            box.Items.OfType<StoreSortOption>().Select(o => o.Label).Should().Equal("Top rated", "Most downloaded", "Recently updated", "Newest", "Name");
            box.SelectedKey.Should().Be("Newest");
            (box.SelectedItem as StoreSortOption)?.Label.Should().Be("Newest");
        });

        await OnUi(window.Close);
    }

    [Fact]
    public async Task PickingAnEntryOfTheSortBox_SetsTheViewModelsSort()
    {
        var viewModel = new StubViewModel([Row("a", "B")], new FakeIcons());
        var (window, view) = await Show(viewModel);

        await EventuallyOnUi(() =>
        {
            Dispatcher.UIThread.RunJobs();
            var box = view.GetVisualDescendants().OfType<StoreSortBox>().Should().ContainSingle().Subject;
            box.SelectedItem = box.Items.OfType<StoreSortOption>().Single(o => o.Label == "Name");
            viewModel.Sort.Should().Be(ThunderstoreSort.Name);
        });

        await OnUi(window.Close);
    }
}
