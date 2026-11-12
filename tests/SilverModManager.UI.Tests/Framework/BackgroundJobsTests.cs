using Avalonia.Threading;
using System.Reactive.Concurrency;
using FluentAssertions;
using ReactiveUI;

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

    /// <summary>
    /// Every test's own awaits and every R3 hop reach the shared dispatcher as Default jobs, so while other tests are
    /// busy there is always one queued. A value handed to <c>RxApp.MainThreadScheduler</c> from a pool thread (what
    /// <c>OnUI()</c> does) used to queue below all of them and wait out the whole burst, tens of seconds on a loaded
    /// run, while pages polled for it. It has to take its turn with them instead.
    /// </summary>
    [Fact]
    public async Task AValueHandedToTheMainThreadSchedulerIsDeliveredWhileOtherTestsKeepThePoolOfDefaultJobsBusy()
    {
        using var stop = new CancellationTokenSource();
        var busy = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
                await Dispatcher.UIThread.InvokeAsync(() => Thread.Sleep(2));
        })).ToArray();

        try
        {
            // ReactiveUI keeps the main-thread scheduler per thread under a test runner; view models read the UI thread's.
            var scheduler = await Dispatcher.UIThread.InvokeAsync(() => RxApp.MainThreadScheduler);
            var delivered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await Task.Run(() => scheduler.Schedule(() => { delivered.TrySetResult(true); }));

            (await Task.WhenAny(delivered.Task, Task.Delay(TimeSpan.FromSeconds(3)))).Should().BeSameAs(delivered.Task,
                "the scheduler queues behind the other jobs of its priority, not behind all of them");
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(busy);
        }
    }
}
