using Avalonia.Threading;
using FluentAssertions;
using SilverModManager.App.UI.Windows.Xdnd;
using SilverModManager.UI.Tests.Framework;

namespace SilverModManager.UI.Tests.Windows.Xdnd;

/// <summary>The real one-shot timer the receiver uses, on the headless dispatcher.</summary>
[Collection(HeadlessUi.Collection)]
public class DispatcherUiTimerTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(20);

    [Fact]
    public async Task ItFiresOnceOnTheUiThreadAndNotAgain()
    {
        var fired = 0;
        var onUiThread = true;
        await OnUi(() =>
        {
            new DispatcherUiTimer().After(Delay, () =>
            {
                fired++;
                onUiThread &= Dispatcher.UIThread.CheckAccess();
            });
        });

        await Eventually(() => fired.Should().Be(1));
        await Task.Delay(Delay * 8);

        fired.Should().Be(1);
        onUiThread.Should().BeTrue();
    }

    [Fact]
    public async Task ADisposedTimerNeverFires()
    {
        var fired = 0;
        await OnUi(() =>
        {
            new DispatcherUiTimer().After(Delay, () => fired++).Dispose();
        });

        await Task.Delay(Delay * 8);

        fired.Should().Be(0);
    }
}
