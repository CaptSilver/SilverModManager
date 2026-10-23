using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.GuidedInstallers;
using NexusMods.Games.FOMOD.CoreDelegates;
using NexusMods.Games.TestFramework;
using NexusMods.Sdk.Jobs;
using Xunit;
using FomodUi = FomodInstaller.Interface.ui;

namespace NexusMods.Games.FOMOD.Tests;

/// <summary>
/// The FOMOD library applies a selection on a task of its own and confirms it by calling
/// <see cref="UiDelegates.UpdateState"/> again. These tests play the library and decide when that
/// confirmation arrives, the way a busy thread pool does.
/// </summary>
public class UiDelegatesTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // Long past the point where the delegates used to stop waiting for the confirmation.
    private static readonly TimeSpan SlowLibrary = TimeSpan.FromSeconds(1);

    private sealed class FakeLibrary
    {
        public readonly ConcurrentQueue<(int Step, int Group, int[] Options)> Selected = new();
        public readonly ConcurrentQueue<(bool Forward, int Step)> Continued = new();
        public int Cancelled;
        private readonly SemaphoreSlim _selectCalls = new(0);
        private readonly TaskCompletionSource _continueCalled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Start(UiDelegates delegates) => delegates.StartDialog(
            "Test",
            new FomodInstaller.Interface.HeaderImage(null, false, 0),
            select: (step, group, options) =>
            {
                Selected.Enqueue((step, group, options));
                _selectCalls.Release();
            },
            cont: (forward, step) =>
            {
                Continued.Enqueue((forward, step));
                _continueCalled.TrySetResult();
            },
            cancel: () => Interlocked.Increment(ref Cancelled));

        public async Task NextSelect() => (await _selectCalls.WaitAsync(Timeout)).Should().BeTrue("the delegates should pass the selection on");

        public Task ContinueCalled => _continueCalled.Task.WaitAsync(Timeout);
    }

    private static FomodUi.InstallerStep[] OneStep(int groups) => Steps(count: 1, groups);

    private static FomodUi.InstallerStep[] Steps(int count, int groups) => Enumerable.Range(0, count)
        .Select(s => new FomodUi.InstallerStep(s, $"Step {s + 1}", visible: true)
        {
            optionalFileGroups = new FomodUi.GroupList
            {
                order = "Explicit",
                group = Enumerable.Range(0, groups)
                    .Select(g => new FomodUi.Group(g, $"Group {g}", "SelectExactlyOne",
                    [
                        new FomodUi.Option(0, "Chosen", "", null, selected: true, preset: false, "Recommended", null),
                        new FomodUi.Option(1, "Other", "", null, selected: false, preset: false, "Optional", null),
                    ]))
                    .ToArray(),
            },
        })
        .ToArray();

    /// <summary>
    /// Runs tasks on the thread pool, but holds up the thread that queues the task after
    /// <see cref="StallNextQueue"/> until <see cref="Release"/> completes, the way the OS can
    /// preempt a thread that just registered a continuation.
    /// </summary>
    private sealed class StallingScheduler : TaskScheduler
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool StallNextQueue;
        public volatile bool Stalled;

        protected override void QueueTask(Task task)
        {
            ThreadPool.UnsafeQueueUserWorkItem(_ => TryExecuteTask(task), null);
            if (!StallNextQueue) return;
            StallNextQueue = false;
            Stalled = true;
            Release.Task.Wait(Timeout);
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task> GetScheduledTasks() => [];
    }

    [Fact]
    public async Task TheNextStep_IsAsked_EvenWhenItArrivesBeforeThePreviousStepHasReturned()
    {
        var window = new ScriptedGuidedInstallerState();
        var delegates = new UiDelegates(NullLogger<UiDelegates>.Instance, window.Create());
        using var _ = delegates;
        var steps = Steps(count: 2, groups: 1);
        var lastStepAnswered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Plays the library: it confirms a selection and moves to the next step on tasks of its own.
        delegates.StartDialog(
            "Test",
            new FomodInstaller.Interface.HeaderImage(null, false, 0),
            select: (step, _, _) => Task.Run(() => delegates.UpdateState(steps, step)),
            cont: (_, step) =>
            {
                if (step == 0) Task.Run(() => delegates.UpdateState(steps, 1));
                else lastStepAnswered.TrySetResult();
            },
            cancel: () => { });

        // The continuation that answers step 1 is queued from inside UpdateState, which then
        // stays stalled until step 2 was asked or the wait gives up.
        var scheduler = new StallingScheduler();
        var firstStep = Task.Factory.StartNew(() =>
        {
            scheduler.StallNextQueue = true;
            delegates.UpdateState(steps, 0);
        }, CancellationToken.None, TaskCreationOptions.None, scheduler);

        try
        {
            var deadline = DateTime.UtcNow + Timeout;
            while (window.Prompts.Single().Steps.Length < 2 && DateTime.UtcNow < deadline)
                await Task.Delay(20);
        }
        finally
        {
            scheduler.Release.TrySetResult();
        }

        await firstStep.WaitAsync(Timeout);
        // Without the stall the answer ran on another scheduler and the race wasn't exercised.
        scheduler.Stalled.Should().BeTrue();
        window.Prompts.Single().Steps.Select(s => s.Name).Should().Equal("Step 1", "Step 2");

        // Step 2's answer runs on other threads; let it finish before the delegates are disposed.
        await lastStepAnswered.Task.WaitAsync(Timeout);
        delegates.Failure.Should().BeNull();
    }

    /// <summary>
    /// Blocks inside <see cref="IGuidedInstaller.RequestUserChoice"/>, so the step being set up holds
    /// the delegates until <see cref="Unblock"/> is set.
    /// </summary>
    private sealed class BlockingGuidedInstaller : IGuidedInstaller
    {
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Unblock = new();
        public bool SupportsUserInteraction => false;
        public void SetupInstaller(string name) { }
        public void SetupInstaller(string name, string? notice) { }
        public void CleanupInstaller() { }
        public void Dispose() { }

        public Task<UserChoice> RequestUserChoice(GuidedInstallationStep installationStep, Percent progress, CancellationToken cancellationToken)
        {
            Entered.Set();
            Unblock.Wait(Timeout);
            return Task.FromResult(new UserChoice(new UserChoice.CancelInstallation()));
        }
    }

    [Fact]
    public async Task AStepThatArrivesWhileAnotherIsBeingSetUp_FailsTheInstallInsteadOfBeingDropped()
    {
        var installer = new BlockingGuidedInstaller();
        var delegates = new UiDelegates(NullLogger<UiDelegates>.Instance, installer);
        using var _ = delegates;
        var library = new FakeLibrary();
        library.Start(delegates);
        var steps = Steps(count: 2, groups: 1);

        var firstStep = Task.Run(() => delegates.UpdateState(steps, 0));
        try
        {
            installer.Entered.Wait(Timeout).Should().BeTrue();
            delegates.UpdateState(steps, 1);

            // A dropped step is never answered, and the library would wait for it forever.
            delegates.Failure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("Step 2");
            library.Cancelled.Should().Be(1);
        }
        finally
        {
            installer.Unblock.Set();
        }

        await firstStep.WaitAsync(Timeout);
    }

    private static (UiDelegates Delegates, FakeLibrary Library) Create()
    {
        var delegates = new UiDelegates(NullLogger<UiDelegates>.Instance, new ScriptedGuidedInstallerState().Create());
        var library = new FakeLibrary();
        library.Start(delegates);
        return (delegates, library);
    }

    [Fact]
    public async Task ASlowSelectionConfirmation_IsAwaitedBeforeContinuing()
    {
        var (delegates, library) = Create();
        using var _ = delegates;
        var steps = OneStep(groups: 1);

        delegates.UpdateState(steps, 0);
        await library.NextSelect();

        await Task.Delay(SlowLibrary);
        library.Continued.Should().BeEmpty("the library hasn't applied the selection yet");

        delegates.UpdateState(steps, 0);
        await library.ContinueCalled;

        library.Selected.Should().ContainSingle().Which.Should().BeEquivalentTo((Step: 0, Group: 0, Options: new[] { 0 }));
        library.Continued.Should().Equal((true, 0));
    }

    [Fact]
    public async Task EachGroupIsSelectedOnlyAfterThePreviousOneWasConfirmed()
    {
        var (delegates, library) = Create();
        using var _ = delegates;
        var steps = OneStep(groups: 2);

        delegates.UpdateState(steps, 0);
        await library.NextSelect();

        await Task.Delay(SlowLibrary);
        library.Selected.Should().HaveCount(1, "the first group's selection isn't confirmed yet");

        delegates.UpdateState(steps, 0);
        await library.NextSelect();

        await Task.Delay(SlowLibrary);
        library.Continued.Should().BeEmpty("the second group's selection isn't confirmed yet");

        delegates.UpdateState(steps, 0);
        await library.ContinueCalled;

        library.Selected.Select(s => s.Group).Should().Equal(0, 1);
        library.Continued.Should().Equal((true, 0));
    }

    [Fact]
    public async Task ASelectionTheLibraryNeverConfirms_FailsTheInstallInsteadOfWaitingForever()
    {
        // The library's select task can throw before it confirms, e.g. on a malformed FOMOD.
        var delegates = new UiDelegates(NullLogger<UiDelegates>.Instance, new ScriptedGuidedInstallerState().Create())
        {
            SelectionConfirmationTimeout = TimeSpan.FromMilliseconds(500),
        };
        using var _ = delegates;
        var library = new FakeLibrary();
        library.Start(delegates);

        delegates.UpdateState(OneStep(groups: 1), 0);
        await library.NextSelect();

        var deadline = DateTime.UtcNow + Timeout;
        while (Volatile.Read(ref library.Cancelled) == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        library.Cancelled.Should().Be(1);
        library.Continued.Should().BeEmpty("continuing would install the options the library had before");
        delegates.Failure.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("Step 1");
    }

    [Fact]
    public async Task FailingWhileWaitingForAConfirmation_CancelsWithoutContinuing()
    {
        var (delegates, library) = Create();
        using var _ = delegates;
        var steps = OneStep(groups: 1);

        delegates.UpdateState(steps, 0);
        await library.NextSelect();

        delegates.Cancel();
        await Task.Delay(SlowLibrary);

        delegates.Failure.Should().BeOfType<OperationCanceledException>();
        library.Cancelled.Should().Be(1);
        library.Continued.Should().BeEmpty();
    }
}
