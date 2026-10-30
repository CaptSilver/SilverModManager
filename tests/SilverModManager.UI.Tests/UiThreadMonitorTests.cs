using System.Reflection;
using Avalonia.Threading;
using FluentAssertions;
using SilverModManager.UI.Tests.Framework;

namespace SilverModManager.UI.Tests;

public class UiThreadMonitorTests(IServiceProvider provider) : AUiTest(provider)
{
    // The stand-in for a render tick that never ends: a UI job that spins until the test lets it go.
    private static ManualResetEventSlim SpinUiThread(out Task finished)
    {
        var release = new ManualResetEventSlim();
        var started = new ManualResetEventSlim();
        finished = Dispatcher.UIThread.InvokeAsync(() =>
        {
            started.Set();
            while (!release.IsSet) Thread.SpinWait(1000);
        }).GetTask();
        started.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the spinning job has to reach the UI thread");
        return release;
    }

    [Fact]
    public async Task ASpinningUiThread_IsReportedWithWhatItWasLastAskedToDo()
    {
        using var monitor = new UiThreadMonitor(stallLimit: TimeSpan.FromSeconds(1), interval: TimeSpan.FromMilliseconds(100));
        monitor.TestStarted("Some.Test");
        monitor.Operation("Render(Window), a headless render tick");
        // Let the first heartbeat land so the stall is measured from a running thread.
        await Dispatcher.UIThread.InvokeAsync(() => { });

        using var release = SpinUiThread(out var finished);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (monitor.StuckMessage is null && DateTime.UtcNow < deadline) await Task.Delay(50);

            monitor.StuckMessage.Should().NotBeNull().And.Contain("Some.Test").And.Contain("Render(Window)").And.Contain("spinning");
            var act = () => monitor.TestStarted("Next.Test");
            act.Should().Throw<UiThreadStuckException>("a test that starts after the thread got stuck fails at once instead of waiting");
        }
        finally
        {
            release.Set();
            await finished;
        }
    }

    [Fact]
    public async Task AnIdleOrBusyButRunningUiThread_IsNotReported()
    {
        using var monitor = new UiThreadMonitor(stallLimit: TimeSpan.FromSeconds(30), interval: TimeSpan.FromMilliseconds(100));

        // Work that keeps returning to the dispatcher, as a slow but finishing render does.
        for (var i = 0; i < 30; i++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Thread.Sleep(50));
            await Task.Delay(50);
        }

        monitor.StuckMessage.Should().BeNull();
    }

    [Fact]
    public async Task WaitingOnAnActionTheUiThreadNeverFinishes_EndsWithAnExplanationNotAHang()
    {
        using var monitor = new UiThreadMonitor(stallLimit: TimeSpan.FromMinutes(5), interval: TimeSpan.FromSeconds(1));
        monitor.Operation("Render(SomeWindow)");
        using var release = SpinUiThread(out var finished);
        try
        {
            var behindTheSpin = Dispatcher.UIThread.InvokeAsync(() => 1).GetTask();

            var act = () => monitor.WithinAsync(behindTheSpin, TimeSpan.FromMilliseconds(300));
            UiThreadMonitor.ActionCap(TimeSpan.FromSeconds(15)).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(2), "a slow UI thread under load must not turn a retry into a failure");

            (await act.Should().ThrowAsync<TimeoutException>()).Which.Message.Should().Contain("Render(SomeWindow)").And.Contain("spinning");
        }
        finally
        {
            release.Set();
            await finished;
        }
    }

    [Fact]
    public void EveryFactAndTheoryInThisProject_CarriesATimeout()
    {
        var fact = typeof(UiThreadMonitorTests).GetMethod(nameof(EveryFactAndTheoryInThisProject_CarriesATimeout))!.GetCustomAttribute<Xunit.FactAttribute>();

        fact.Should().BeOfType<UiFactAttribute>();
        fact!.Timeout.Should().Be(UiFactAttribute.Limit).And.BePositive();
        new UiTheoryAttribute().Timeout.Should().Be(UiFactAttribute.Limit);
    }
}
