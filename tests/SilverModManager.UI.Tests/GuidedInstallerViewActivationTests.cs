using Avalonia.Controls;
using Avalonia.Threading;
using FluentAssertions;
using SilverModManager.Games.FOMOD.UI;

namespace SilverModManager.UI.Tests;

public class GuidedInstallerViewActivationTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public async Task OptionView_ActivatedWithoutAViewModel_DoesNothing()
    {
        var view = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var view = new GuidedInstallerOptionView();
            ShowRendered(Track(new Window { Width = 400, Height = 200, Content = view }));
            return view;
        });

        await Dispatcher.UIThread.InvokeAsync(() => view.IsLoaded.Should().BeTrue());
    }
}
