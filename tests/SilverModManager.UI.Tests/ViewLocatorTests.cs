using Avalonia.Controls;
using FluentAssertions;
using SilverModManager.App.UI;
using SilverModManager.UI.Sdk;

namespace SilverModManager.UI.Tests;

public class ViewLocatorTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class ViewlessViewModel : IViewModel
    {
        public ReactiveUI.ViewModelActivator Activator { get; } = new();
        public Type ViewModelInterface => typeof(ViewlessViewModel);
    }

    [Fact]
    public void Test_Build_WithoutAViewModelNamesTheMissingViewModel()
    {
        var act = () => new ViewLocator().Build(null);

        act.Should().Throw<InvalidOperationException>().WithMessage("*view model*");
    }

    [Fact]
    public async Task Test_Build_ForAViewModelWithoutAViewShowsNotFound()
    {
        var text = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
            () => ((TextBlock)new ViewLocator().Build(new ViewlessViewModel())).Text);

        text.Should().StartWith("Not Found: ");
    }
}
