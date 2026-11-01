using Avalonia;
using Avalonia.Input;
using FluentAssertions;
using SilverModManager.App.UI.Windows.Xdnd;

namespace SilverModManager.UI.Tests.Windows.Xdnd;

/// <summary>The end of a drag: a drop in each phase, the Finished reply, and the window closing mid-drag.</summary>
public class XdndSessionDropTests : XdndSessionTestBase
{
    private static readonly TimeSpan DataTimeout = XdndSession.DataTimeout;
    private static readonly TimeSpan RetryDelay = XdndSession.RetryDelay;

    private nint FirstProperty => _atoms.DataProperties[0];

    private IEnumerable<SentClientMessage> Sent(nint type) => _x.SentMessages.Where(m => m.MessageType == type);

    private SentClientMessage OnlyFinished() => Sent(_atoms.XdndFinished).Should().ContainSingle().Subject;

    private void FetchFilesWhileDropping()
    {
        EnterWithFiles();
        _session.Handle(Position(Source, 300, 200));
        _session.Handle(Drop(Source));
    }

    // Drop once the window has the files

    [Fact]
    public void ADropOnceReadyDropsOnTheSinkAtTheLastPointAndFinishesCopyToTheSource()
    {
        FetchFiles();
        _session.Handle(Position(Source, 320, 210));

        _session.Handle(Drop(Source));

        _sink.Calls.Last().Should().Match<SinkCall>(c =>
            c.Kind == "Drop" && c.At == new PixelPoint(320, 210) && c.Allowed == DragDropEffects.Copy);
        _sink.Calls.Last().Data.Should().BeSameAs(_files.Created.Single());
        OnlyFinished().Should().Be(new SentClientMessage(Source, _atoms.XdndFinished, Target, 1, _atoms.XdndActionCopy, 0, 0));
        _session.Phase.Should().Be(XdndPhase.Idle);
    }

    [Fact]
    public void TheDroppedSinkIsNotToldToLeaveAfterwards()
    {
        FetchFiles();

        _session.Handle(Drop(Source));

        _sink.Kinds.Should().Equal("Enter", "Drop");
    }

    [Fact]
    public void TheDataPropertyIsDeletedOnceTheDropIsFinished()
    {
        FetchFiles();
        _x.DeletedProperties.Clear();

        _session.Handle(Drop(Source));

        _x.DeletedProperties.Should().Contain((Target, FirstProperty));
    }

    [Fact]
    public void ASinkThatRefusesTheDropGetsAFinishedWithTheAcceptBitClear()
    {
        FetchFiles();
        _sink.Answer = DragDropEffects.None;

        _session.Handle(Drop(Source));

        OnlyFinished().Should().Be(new SentClientMessage(Source, _atoms.XdndFinished, Target, 0, 0, 0, 0));
    }

    [Fact]
    public void AMoveDropFinishesWithTheMoveAction()
    {
        EnterWithFiles();
        _session.Handle(Position(Source, 300, 200, action: _atoms.XdndActionMove));
        var property = _x.Conversions[^1].Property;
        Offer(property, "file:///home/u/a.zip\r\n");
        _session.Handle(Notify(property));
        _sink.Answer = DragDropEffects.Move;

        _session.Handle(Drop(Source));

        OnlyFinished().L2.Should().Be(_atoms.XdndActionMove);
    }

    [Fact]
    public void ADropFromAnotherWindowIsIgnored()
    {
        FetchFiles();

        _session.Handle(Drop(OtherSource));

        _sink.Kinds.Should().Equal("Enter");
        Sent(_atoms.XdndFinished).Should().BeEmpty();
        _session.Phase.Should().Be(XdndPhase.Ready);
    }

    [Fact]
    public void AFinishedDropStartsFromScratchSoTheNextDragAsksForItsOwnData()
    {
        FetchFiles();
        _session.Handle(Drop(Source));

        _session.Handle(Enter(Source, Version5, _atoms.TextUriList));
        _session.Handle(Position(Source, 10, 10));

        _x.Conversions.Should().HaveCount(2);
        _session.Phase.Should().Be(XdndPhase.FetchingData);
    }

    // Drop when there is nothing to hand over

    [Fact]
    public void ADropBeforeAnyPositionFinishesRefusedAtOnceAndTouchesNoSink()
    {
        EnterWithFiles();

        _session.Handle(Drop(Source));

        OnlyFinished().L1.Should().Be(0);
        _x.Conversions.Should().BeEmpty();
        _sink.Calls.Should().BeEmpty();
        _session.Phase.Should().Be(XdndPhase.Idle);
    }

    [Fact]
    public void ADropOfADragWithoutFilesFinishesRefused()
    {
        _session.Handle(Enter(Source, Version5, 9999));

        _session.Handle(Drop(Source));

        OnlyFinished().L1.Should().Be(0);
        _sink.Calls.Should().BeEmpty();
        _session.Phase.Should().Be(XdndPhase.Idle);
    }

    [Fact]
    public void ADropWithNoDragInProgressStillAnswersTheSourceThatSentIt()
    {
        _session.Handle(Drop(Source));

        OnlyFinished().Should().Be(new SentClientMessage(Source, _atoms.XdndFinished, Target, 0, 0, 0, 0));
        _session.Phase.Should().Be(XdndPhase.Idle);
    }

    [Fact]
    public void ADropRightAfterATimedOutFetchFinishesRefused()
    {
        EnterWithFiles();
        _session.Handle(Position(Source, 300, 200));
        _timer.Fire(DataTimeout);

        _session.Handle(Drop(Source));

        OnlyFinished().L1.Should().Be(0);
        _sink.Calls.Should().BeEmpty();
        _session.Phase.Should().Be(XdndPhase.Idle);
    }

    // Drop before the data arrived

    [Fact]
    public void ADropWhileFetchingWaitsAndSendsNothingYet()
    {
        FetchFilesWhileDropping();

        _session.Phase.Should().Be(XdndPhase.Dropped);
        _x.SentMessages.Should().BeEmpty();
        _sink.Calls.Should().BeEmpty();
    }

    [Fact]
    public void WhenTheDataComesAfterTheDropTheSinkIsEnteredThenDroppedOnThenTheSourceIsToldFinished()
    {
        var finishedBeforeDrop = true;
        _sink.OnDrop = () => finishedBeforeDrop = Sent(_atoms.XdndFinished).Any();
        FetchFilesWhileDropping();
        Offer(FirstProperty, "file:///home/u/a.zip\r\n");

        _session.Handle(Notify(FirstProperty));

        _sink.Kinds.Should().Equal("Enter", "Drop");
        _sink.Calls[0].At.Should().Be(new PixelPoint(300, 200));
        finishedBeforeDrop.Should().BeFalse();
        OnlyFinished().Should().Be(new SentClientMessage(Source, _atoms.XdndFinished, Target, 1, _atoms.XdndActionCopy, 0, 0));
        Sent(_atoms.XdndStatus).Should().BeEmpty();
        _session.Phase.Should().Be(XdndPhase.Idle);
    }

    [Fact]
    public void ADroppedIncrTransferCompletesIntoEnterDropFinished()
    {
        FetchFilesWhileDropping();
        _x.SetProperty(Target, FirstProperty, new XProperty(_atoms.Incr, 32, new byte[8]));
        _session.Handle(Notify(FirstProperty));
        _x.SetProperty(Target, FirstProperty, new XProperty(_atoms.TextUriList, 8, "file:///home/u/a.zip\r\n"u8.ToArray()));
        _session.Handle(NewValue(FirstProperty));
        _x.SetProperty(Target, FirstProperty, new XProperty(_atoms.TextUriList, 8, []));

        _session.Handle(NewValue(FirstProperty));

        _sink.Kinds.Should().Equal("Enter", "Drop");
        OnlyFinished().L1.Should().Be(1);
    }

    [Fact]
    public void ADropWhileTheFilesAreBeingOpenedFinishesOnceTheyAre()
    {
        _files.Hold();
        FetchFilesWhileDropping();
        Offer(FirstProperty, "file:///home/u/a.zip\r\n");
        _session.Handle(Notify(FirstProperty));
        _sink.Calls.Should().BeEmpty();

        _files.Release();

        _sink.Kinds.Should().Equal("Enter", "Drop");
        OnlyFinished().L1.Should().Be(1);
    }

    [Fact]
    public void ADropWhileTheFilesAreOpeningThatTimesOutFinishesRefused()
    {
        _files.Hold();
        FetchFilesWhileDropping();
        Offer(FirstProperty, "file:///home/u/a.zip\r\n");
        _session.Handle(Notify(FirstProperty));

        _timer.Fire(DataTimeout);
        _files.Release();

        OnlyFinished().L1.Should().Be(0);
        _sink.Calls.Should().BeEmpty();
        _session.Phase.Should().Be(XdndPhase.Idle);
    }

    [Fact]
    public void WhenTheDataTimesOutAfterADropTheSourceGetsAFinishedRefusalAndNoStatus()
    {
        FetchFilesWhileDropping();

        _timer.Fire(DataTimeout);

        OnlyFinished().Should().Be(new SentClientMessage(Source, _atoms.XdndFinished, Target, 0, 0, 0, 0));
        Sent(_atoms.XdndStatus).Should().BeEmpty();
        _sink.Calls.Should().BeEmpty();
        _session.Phase.Should().Be(XdndPhase.Idle);
    }

    [Fact]
    public void ARefusedConversionAfterADropFinishesRefusedWithoutAQuickRetry()
    {
        FetchFilesWhileDropping();

        _session.Handle(Notify(0));

        OnlyFinished().L1.Should().Be(0);
        _timer.Live.Should().BeEmpty();
        _x.Conversions.Should().ContainSingle();
        _session.Phase.Should().Be(XdndPhase.Idle);
    }

    [Fact]
    public void ARetryAlreadyArmedBeforeTheDropStillAsksAgainAndTheDropCompletesWithItsAnswer()
    {
        EnterWithFiles();
        _session.Handle(Position(Source, 300, 200));
        _session.Handle(Notify(0));
        _session.Handle(Drop(Source));

        _timer.Fire(RetryDelay);
        var property = _x.Conversions[^1].Property;
        Offer(property, "file:///home/u/a.zip\r\n");
        _session.Handle(Notify(property));

        _x.Conversions.Should().HaveCount(2);
        _sink.Kinds.Should().Equal("Enter", "Drop");
        OnlyFinished().L1.Should().Be(1);
    }

    [Fact]
    public void ADroppedFetchStopsAtTheFinishedSoALateNotifyIsOnlyDeleted()
    {
        FetchFilesWhileDropping();
        _timer.Fire(DataTimeout);
        _x.DeletedProperties.Clear();

        _session.Handle(Notify(FirstProperty));

        _x.DeletedProperties.Should().Equal((Target, FirstProperty));
        _sink.Calls.Should().BeEmpty();
    }

    [Fact]
    public void APositionAfterTheDropIsIgnored()
    {
        FetchFilesWhileDropping();

        _session.Handle(Position(Source, 10, 10));

        _x.Conversions.Should().ContainSingle();
        _x.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public void ASinkThatThrowsOnDropStillGetsTheSourceAFinishedRefusal()
    {
        FetchFiles();
        _sink.OnDrop = () => throw new InvalidOperationException("window failed");

        _session.Handle(Drop(Source));

        OnlyFinished().L1.Should().Be(0);
        _session.Phase.Should().Be(XdndPhase.Idle);
    }

    // Cancel

    [Fact]
    public void CancelOnceReadyLeavesTheSinkAndGoesIdle()
    {
        FetchFiles();

        _session.Cancel();

        _sink.Kinds.Should().Equal("Enter", "Leave");
        _session.Phase.Should().Be(XdndPhase.Idle);
    }

    [Fact]
    public void CancelWhileFetchingDisposesEveryTimerAndNeverEntersTheSink()
    {
        EnterWithFiles();
        _session.Handle(Position(Source, 300, 200));
        _session.Handle(Notify(0));

        _session.Cancel();
        Offer(FirstProperty, "file:///home/u/a.zip\r\n");
        _session.Handle(Notify(FirstProperty));

        _timer.Live.Should().BeEmpty();
        _sink.Calls.Should().BeEmpty();
        _session.Phase.Should().Be(XdndPhase.Idle);
        var status = OnlyStatus();
        (status.Destination, status.L1 & 1, status.L4).Should().Be((Source, 0, 0));
    }

    [Fact]
    public void CancelInsideTheSinksDropSendsExactlyOneFinishedToTheSource()
    {
        FetchFiles();
        _sink.OnDrop = _session.Cancel;

        _session.Handle(Drop(Source));

        OnlyFinished().Destination.Should().Be(Source);
        _session.Phase.Should().Be(XdndPhase.Idle);
    }

    [Fact]
    public void CancelInsideTheSinksEnterSendsNoStatusToWindowZeroAndStaysIdle()
    {
        _files.Hold();
        FetchFiles();
        _sink.OnEnter = _session.Cancel;

        _files.Release();

        _x.SentMessages.Should().OnlyContain(m => m.Destination == Source);
        _session.Phase.Should().Be(XdndPhase.Idle);
    }

    [Fact]
    public void CancelWhileTheFilesAreBeingOpenedNeverEntersTheSinkEvenWhenTheyFinish()
    {
        _files.Hold();
        FetchFiles();

        _session.Cancel();
        _files.Release();

        _sink.Calls.Should().BeEmpty();
        _timer.Live.Should().BeEmpty();
    }

    [Fact]
    public void CancelAfterADropWasHeldTellsTheSourceFinishedRefusedAndLeavesNothingPending()
    {
        FetchFilesWhileDropping();

        _session.Cancel();

        OnlyFinished().L1.Should().Be(0);
        _timer.Live.Should().BeEmpty();
        _session.Phase.Should().Be(XdndPhase.Idle);
    }

    [Fact]
    public void EventsQueuedBeforeTheCloseButHandledAfterCancelAreIgnored()
    {
        _session.Cancel();

        _session.Handle(Enter(Source, Version5, _atoms.TextUriList));
        _session.Handle(Position(Source, 300, 200));
        _session.Handle(Drop(Source));

        _session.Phase.Should().Be(XdndPhase.Idle);
        _x.SentMessages.Should().BeEmpty();
        _x.Conversions.Should().BeEmpty();
        _sink.Calls.Should().BeEmpty();
    }

    [Fact]
    public void CancelWithNoDragInProgressDoesNothing()
    {
        _session.Cancel();

        _x.Calls.Should().BeEmpty();
        _sink.Calls.Should().BeEmpty();
    }

    [Fact]
    public void CancelTwiceLeavesTheSinkOnce()
    {
        FetchFiles();

        _session.Cancel();
        _session.Cancel();

        _sink.Kinds.Should().Equal("Enter", "Leave");
    }
}
