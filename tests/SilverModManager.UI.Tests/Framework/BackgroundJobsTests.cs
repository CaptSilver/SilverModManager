using Avalonia.Threading;
using FluentAssertions;

namespace SilverModManager.UI.Tests.Framework;

/// <summary>
/// View models deliver values through a scheduler that posts at Background priority; a poll that only reads the page
/// has to run those jobs itself, because on the shared dispatcher they can sit behind other tests' work for a long time.
/// </summary>
public class BackgroundJobsTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public async Task AssertAfterDrain_RunsJobsPostedAtBackgroundPriorityBeforeAsserting()
    {
        var delivered = false;

        await OnUi(() =>
        {
            Dispatcher.UIThread.Post(() => delivered = true, DispatcherPriority.Background);
            // This job holds the UI thread, so the posted one cannot have run unless the helper drains it.
            AssertAfterDrain(() => delivered.Should().BeTrue());
        });
    }
}
