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
    private sealed class StubViewModel(IReadOnlyList<ThunderstorePackageRow> packages, IThunderstoreIconLoader icons)
        : APageViewModel<IThunderstoreViewModel>(new DesignWindowManager()), IThunderstoreViewModel
    {
        public LoadoutId LoadoutId { get; set; }
        public string SearchText { get; set; } = "";
        public bool ShowDeprecated { get; set; }
        public IReadOnlyList<ThunderstorePackageRow> Packages { get; } = packages;
        public IThunderstoreIconLoader Icons { get; } = icons;
        public string ErrorText => "";
        public bool IsRefreshing => false;
        public ReactiveCommand<Unit, Unit> RefreshCommand { get; } = ReactiveCommand.Create(() => { });
        public ReactiveCommand<ThunderstorePackageRow, Unit> OpenPackageCommand { get; } = ReactiveCommand.Create<ThunderstorePackageRow>(_ => { });
        public ReactiveCommand<ThunderstorePackageRow, Unit> InstallCommand { get; } = ReactiveCommand.Create<ThunderstorePackageRow>(_ => { });
    }

    private sealed class FakeIcons : IThunderstoreIconLoader
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

            var icon = view.GetVisualDescendants().OfType<ThunderstoreIcon>().Should().ContainSingle().Subject;
            icon.Source.Should().BeSameAs(icons.Loaded);
            icon.Width.Should().Be(64);
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
            view.GetVisualDescendants().OfType<ThunderstoreIcon>().Should().ContainSingle().Which.Source.Should().BeSameAs(icons.Placeholder);
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

    private sealed class PendingIcons : IThunderstoreIconLoader
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
}
