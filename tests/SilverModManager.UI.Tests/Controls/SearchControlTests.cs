using Avalonia.Controls;
using FluentAssertions;
using R3;
using FilterBase = SilverModManager.App.UI.Controls.Filters.Filter;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Controls.Filters;
using SilverModManager.App.UI.Controls.Search;
using SilverModManager.UI.Tests.Pages;

namespace SilverModManager.UI.Tests.Controls;

/// <summary>The search box applies its text to whichever adapter it is bound to, whenever the text or the adapter changes.</summary>
public class SearchControlTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class FakeAdapter : ISearchableTreeDataGridAdapter
    {
        public ReactiveProperty<Filter> Filter { get; } = new(FilterBase.NoFilter.Instance);
    }

    private async Task<(Window Window, SearchControl Control, TextBox Box)> Show()
    {
        return await OnUi(() =>
        {
            var control = new SearchControl();
            var window = Track(new Window { Width = 600, Height = 200, Content = control });
            ShowRendered(window);
            return Task.FromResult((window, control, ViewProbe.Named<TextBox>(control, "SearchTextBox")));
        });
    }

    [Fact]
    public async Task TextTypedBeforeTheAdapterIsBoundReachesItOnceItIs()
    {
        var (window, control, box) = await Show();
        var adapter = new FakeAdapter();
        await OnUi(() =>
        {
            control.Adapter = null;
            box.Text = "upscaler";
        });
        await Task.Delay(300);

        await OnUi(() => control.Adapter = adapter);

        await EventuallyOnUi(() => adapter.Filter.Value.Should().Be(new Filter.TextFilter("upscaler")));
        await OnUi(window.Close);
    }

    [Fact]
    public async Task SwappingTheAdapterAppliesTheTextToTheNewOne()
    {
        var (window, control, box) = await Show();
        var first = new FakeAdapter();
        var second = new FakeAdapter();
        await OnUi(() =>
        {
            control.Adapter = first;
            box.Text = "upscaler";
        });
        await EventuallyOnUi(() => first.Filter.Value.Should().Be(new Filter.TextFilter("upscaler")));

        await OnUi(() => control.Adapter = second);

        await EventuallyOnUi(() => second.Filter.Value.Should().Be(new Filter.TextFilter("upscaler")));
        await OnUi(window.Close);
    }

    [Fact]
    public async Task ClearingTheTextRemovesTheFilter()
    {
        var (window, control, box) = await Show();
        var adapter = new FakeAdapter();
        await OnUi(() =>
        {
            control.Adapter = adapter;
            box.Text = "upscaler";
        });
        await EventuallyOnUi(() => adapter.Filter.Value.Should().BeOfType<Filter.TextFilter>());

        await OnUi(() => box.Text = "");

        await EventuallyOnUi(() => adapter.Filter.Value.Should().Be(Filter.NoFilter.Instance));
        await OnUi(window.Close);
    }
}
