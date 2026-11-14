using FluentAssertions;
using R3;
using Microsoft.Extensions.Time.Testing;
using SilverModManager.App.UI.Input.Reorder;

namespace SilverModManager.UI.Tests.Input.Reorder;

public class ReorderSessionTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    /// <summary>A list of <paramref name="count"/> rows where every move is applied to <see cref="Index"/> and recorded.</summary>
    private sealed class FakeReorderable(int count, int index) : IReorderable<string>
    {
        public readonly List<(string Item, int Delta)> Moves = [];
        public int Index = index;
        public TaskCompletionSource? Gate;
        public Exception? FailWith;

        public int CountFor(string item) => count;

        public int IndexOf(string item) => Index;

        public async Task MoveBy(string item, int delta, CancellationToken token)
        {
            Moves.Add((item, delta));
            if (Gate is { } gate) await gate.Task;
            if (FailWith is { } failure) throw failure;
            Index = Math.Clamp(Index + delta, 0, count - 1);
        }
    }

    private static (ReorderSession<string> Session, FakeReorderable List, FakeTimeProvider Time) Create(int count = 10, int index = 2)
    {
        var list = new FakeReorderable(count, index);
        var time = new FakeTimeProvider();
        // The settle timer fires on a pool thread in the app and the session posts to the UI thread; tests run the post inline.
        return (new ReorderSession<string>(list, time, Settle, post: action => action()), list, time);
    }

    [Fact]
    public async Task FiveNudgesWithinTheSettleWindowCommitOneMoveOfFive()
    {
        var (session, list, time) = Create();
        session.Start("row");

        for (var i = 0; i < 5; i++)
        {
            session.Nudge(1);
            time.Advance(TimeSpan.FromMilliseconds(20));
        }

        list.Moves.Should().BeEmpty("each nudge restarts the settle timer");
        time.Advance(Settle);
        await session.Idle;

        list.Moves.Should().Equal(("row", 5));
    }

    [Fact]
    public async Task ANudgeDuringAnInFlightMoveWaitsForIt()
    {
        var (session, list, time) = Create();
        list.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Start("row");
        session.Nudge(1);
        time.Advance(Settle);
        list.Moves.Should().Equal(("row", 1));

        session.Nudge(1);
        session.Nudge(1);
        time.Advance(Settle);
        list.Moves.Should().HaveCount(1, "the first move has not finished");

        var gate = list.Gate;
        list.Gate = null;
        gate.SetResult();
        await session.Idle;

        list.Moves.Should().Equal(("row", 1), ("row", 2));
    }

    [Fact]
    public async Task DropCommitsImmediatelyAndTheSettleTimerDoesNotCommitAgain()
    {
        var (session, list, time) = Create();
        session.Start("row");
        session.Nudge(3);

        await session.Drop();
        time.Advance(Settle * 2);
        await session.Idle;

        list.Moves.Should().Equal(("row", 3));
        session.IsLive.Should().BeFalse();
    }

    [Fact]
    public async Task CancelMovesBackToTheStartIndex()
    {
        var (session, list, time) = Create();
        session.Start("row");
        session.Nudge(3);
        time.Advance(Settle);
        await session.Idle;
        list.Moves.Should().Equal(("row", 3));

        await session.Cancel();

        list.Moves.Should().Equal(("row", 3), ("row", -3));
        session.IsLive.Should().BeFalse();
    }

    [Fact]
    public async Task CancelBeforeAnythingWasCommittedMovesNothing()
    {
        var (session, list, _) = Create();
        session.Start("row");
        session.Nudge(3);

        await session.Cancel();

        list.Moves.Should().BeEmpty();
        session.IsLive.Should().BeFalse();
    }

    [Fact]
    public void NudgePastTheEndClampsToTheBottom()
    {
        var (session, _, _) = Create(count: 5, index: 3);
        session.Start("row");

        session.Nudge(10);

        session.TargetIndex.Should().Be(4);
    }

    [Fact]
    public void NudgePastTheTopClampsToTheTop()
    {
        var (session, _, _) = Create(count: 5, index: 1);
        session.Start("row");

        session.Nudge(-10);

        session.TargetIndex.Should().Be(0);
    }

    [Fact]
    public async Task AFailedCommitEndsTheSessionDiscardsQueuedNudgesAndReportsTheRealIndex()
    {
        var (session, list, time) = Create();
        list.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        list.FailWith = new InvalidOperationException("the order changed");
        var failures = new List<ReorderFailure>();
        using var _ = session.Failed.Subscribe(failures.Add);
        session.Start("row");
        session.Nudge(2);
        time.Advance(Settle);

        // A press that arrives while the commit is in flight, before it fails.
        session.Nudge(3);
        time.Advance(Settle);
        list.Gate.SetResult();
        await session.Idle;

        list.Moves.Should().Equal(("row", 2));
        var failure = failures.Should().ContainSingle().Subject;
        failure.ActualIndex.Should().Be(2);
        failure.WasCancel.Should().BeFalse();
        failure.Error.Should().BeSameAs(list.FailWith, "the page's own error goes to the log");
        session.IsLive.Should().BeFalse();
        session.TargetIndex.Should().Be(2, "the lifted row snaps back to where the row really is");

        session.Nudge(1);
        await session.Drop();
        await session.Cancel();
        time.Advance(Settle);
        await session.Idle;
        list.Moves.Should().HaveCount(1, "an ended session is inert");
        failures.Should().HaveCount(1);
    }

    [Fact]
    public async Task AFailedCancelSaysSo()
    {
        var (session, list, time) = Create();
        var failures = new List<ReorderFailure>();
        using var _ = session.Failed.Subscribe(failures.Add);
        session.Start("row");
        session.Nudge(3);
        time.Advance(Settle);
        await session.Idle;
        list.FailWith = new InvalidOperationException("gone");

        await session.Cancel();

        var failure = failures.Should().ContainSingle().Subject;
        failure.ActualIndex.Should().Be(5);
        failure.WasCancel.Should().BeTrue();
        failure.Error.Should().BeSameAs(list.FailWith);
    }

    [Fact]
    public async Task ADropOfARowThatNeverMovedCommitsNothing()
    {
        var (session, list, _) = Create();
        session.Start("row");

        await session.Drop();

        list.Moves.Should().BeEmpty();
        session.IsLive.Should().BeFalse();
    }
}
