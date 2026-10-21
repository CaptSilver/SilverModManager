using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Games.FOMOD.CoreDelegates;
using NexusMods.Games.TestFramework;
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

    private static FomodUi.InstallerStep[] OneStep(int groups)
    {
        var step = new FomodUi.InstallerStep(0, "Step 1", visible: true)
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
        };
        return [step];
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
