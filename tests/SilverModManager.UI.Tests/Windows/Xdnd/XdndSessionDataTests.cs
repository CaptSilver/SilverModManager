using Avalonia;
using Avalonia.Input;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using SilverModManager.App.UI.Windows.Xdnd;

namespace SilverModManager.UI.Tests.Windows.Xdnd;

/// <summary>
/// The half of a drag where the session fetches the uri list from the source and then reports the verdict: direct
/// and INCR transfers, a source that refuses, the deadlines, and notifies that arrive late.
/// </summary>
public class XdndSessionDataTests : XdndSessionTestBase
{
    private static readonly TimeSpan DataTimeout = XdndSession.DataTimeout;
    private static readonly TimeSpan RetryDelay = XdndSession.RetryDelay;

    private nint FirstProperty => _atoms.DataProperties[0];
    private nint SecondProperty => _atoms.DataProperties[1];

    private SentClientMessage LastStatus() => _x.SentMessages.Last(m => m.MessageType == _atoms.XdndStatus);

    // The reply path

    [Fact]
    public void ADirectReplyIsReadAndDeletedInOneRequestAndTheSinkIsEnteredAtTheTranslatedPoint()
    {
        EnterWithFiles();
        _session.Handle(Position(Source, 300, 200));
        Offer(FirstProperty, "file:///home/u/a.zip\r\n");

        _session.Handle(Notify(FirstProperty));

        _x.PropertyReads.Should().ContainSingle().Which.Should().Be(new PropertyRead(Target, FirstProperty, true));
        _sink.Calls.Should().ContainSingle().Which.Should().Match<SinkCall>(c =>
            c.Kind == "Enter" && c.At == new PixelPoint(300, 200) && c.Allowed == DragDropEffects.Copy);
        _session.Phase.Should().Be(XdndPhase.Ready);
    }

    [Fact]
    public void TheParsedLocalPathsAreWhatTheFileFactoryIsAskedFor()
    {
        FetchFiles("file:///home/u/a%20b.zip\r\nsmb://nas/x\r\nfile:///home/u/c.zip\r\n");

        _files.Requests.Should().ContainSingle().Which.Should().Equal("/home/u/a b.zip", "/home/u/c.zip");
    }

    [Fact]
    public void TheSinkGetsTheDataObjectTheFactoryBuilt()
    {
        FetchFiles();

        _sink.Calls.Single().Data.Should().BeSameAs(_files.Created.Single());
    }

    [Fact]
    public void TheStatusGoesToTheSourceFromTheTargetAcceptingWithCopy()
    {
        FetchFiles();

        OnlyStatus().Should().Be(new SentClientMessage(
            Source, _atoms.XdndStatus, Target, 3, 0, 0, _atoms.XdndActionCopy));
    }

    [Fact]
    public void ASinkThatRefusesGivesAStatusWithTheAcceptBitClearAndNoAction()
    {
        _sink.Answer = DragDropEffects.None;

        FetchFiles();

        OnlyStatus().Should().Be(new SentClientMessage(Source, _atoms.XdndStatus, Target, 2, 0, 0, 0));
        _session.Phase.Should().Be(XdndPhase.Ready, "the window saw the drag and said no; later positions still go to it");
    }

    [Fact]
    public void AMoveDragIsOfferedToTheSinkAsMoveAndAnsweredWithMove()
    {
        _sink.Answer = DragDropEffects.Move;
        EnterWithFiles();
        _session.Handle(Position(Source, 10, 10, action: _atoms.XdndActionMove));
        Offer(FirstProperty, "file:///a\r\n");

        _session.Handle(Notify(FirstProperty));

        _sink.Calls.Single().Allowed.Should().Be(DragDropEffects.Move);
        OnlyStatus().L4.Should().Be(_atoms.XdndActionMove);
    }

    [Fact]
    public void ThePointAndActionOfTheLatestPositionWhileFetchingAreWhatTheSinkSees()
    {
        EnterWithFiles();
        _session.Handle(Position(Source, 10, 10));
        _session.Handle(Position(Source, 40, 50, action: _atoms.XdndActionLink));
        Offer(FirstProperty, "file:///a\r\n");

        _session.Handle(Notify(FirstProperty));

        _sink.Calls.Single().Should().Match<SinkCall>(c => c.At == new PixelPoint(40, 50) && c.Allowed == DragDropEffects.Link);
    }

    [Fact]
    public void APositionOnceReadyGoesToOverWithTheSameDataAndGetsItsOwnStatus()
    {
        FetchFiles();
        _sink.Answer = DragDropEffects.Move;

        _session.Handle(Position(Source, 20, 30, action: _atoms.XdndActionMove));

        _sink.Calls.Last().Should().Match<SinkCall>(c =>
            c.Kind == "Over" && c.At == new PixelPoint(20, 30) && c.Allowed == DragDropEffects.Move);
        _sink.Calls.Last().Data.Should().BeSameAs(_files.Created.Single());
        _x.SentMessages.Should().HaveCount(2);
        LastStatus().L4.Should().Be(_atoms.XdndActionMove);
    }

    [Fact]
    public void APositionOnceReadyFromAnotherWindowDoesNotReachTheSink()
    {
        FetchFiles();

        _session.Handle(Position(OtherSource, 20, 30));

        _sink.Kinds.Should().Equal("Enter");
        _x.SentMessages.Should().HaveCount(1);
    }

    [Fact]
    public void AListOfNothingLocalStillEntersTheSinkWithAnEmptyListAndLogsTheSkippedUris()
    {
        FetchFiles("smb://nas/x\r\ntrash:/y\r\n");

        _files.Requests.Single().Should().BeEmpty();
        _sink.Kinds.Should().Equal("Enter");
        _log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information && e.Message.Contains("smb://nas/x") && e.Message.Contains("not a local file"));
    }

    [Fact]
    public void PathsTheFactoryCouldNotOpenAreLoggedAtInformation()
    {
        _files.Missing = ["/home/u/gone.zip"];

        FetchFiles();

        _log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information && e.Message.Contains("/home/u/gone.zip"));
    }

    [Fact]
    public void WithoutSkippedUrisOrMissingPathsNothingIsLoggedAtInformation()
    {
        FetchFiles();

        _log.Entries.Should().NotContain(e => e.Level >= LogLevel.Information);
    }

    [Fact]
    public void TheDeadlineIsDisposedOnceTheDataIsComplete()
    {
        FetchFiles();

        _timer.Live.Should().BeEmpty();
    }

    [Fact]
    public void AFileFactoryThatThrowsStillAnswersTheOwedStatusWithARefusalAndLogsAWarning()
    {
        _files.Failure = new IOException("disk gone");

        FetchFiles();

        OnlyStatus().Should().Be(new SentClientMessage(Source, _atoms.XdndStatus, Target, 2, 0, 0, 0));
        _sink.Calls.Should().BeEmpty();
        _session.Phase.Should().Be(XdndPhase.Entered);
        _log.Entries.Should().Contain(e => e.Level == LogLevel.Warning);
        _timer.Live.Should().BeEmpty();
    }

    // Without files

    [Fact]
    public void ADragWithoutAUriListNeverTouchesTheSink()
    {
        _session.Handle(Enter(Source, Version5, 77, 78, 79));

        _session.Handle(Position(Source, 10, 10));
        _session.Handle(Leave(Source));

        _sink.Calls.Should().BeEmpty();
        _x.Conversions.Should().BeEmpty();
    }

    // A source that refuses

    [Fact]
    public void ARefusedConversionSendsNoStatusAndArmsTheQuickRetry()
    {
        EnterWithFiles();
        _session.Handle(Position(Source, 10, 10));

        _session.Handle(Notify(0));

        _x.SentMessages.Should().BeEmpty();
        _timer.Live.Select(h => h.Delay).Should().BeEquivalentTo([DataTimeout, RetryDelay]);
        _session.Phase.Should().Be(XdndPhase.FetchingData);
    }

    [Fact]
    public void TheQuickRetryAsksAgainOnTheOtherPropertyWithTheSameTimeAndAnswersWithoutWaitingForMotion()
    {
        EnterWithFiles();
        _session.Handle(Position(Source, 10, 10, time: 4242));
        _session.Handle(Notify(0));

        _timer.Fire(RetryDelay);

        _x.Conversions.Should().HaveCount(2);
        _x.Conversions[1].Should().Be(new SelectionConversion(_atoms.XdndSelection, _atoms.TextUriList, SecondProperty, Target, 4242));
        _x.SentMessages.Should().BeEmpty();

        Offer(SecondProperty, "file:///a\r\n");
        _session.Handle(Notify(SecondProperty));

        _sink.Kinds.Should().Equal("Enter");
        OnlyStatus().L1.Should().Be(3);
        _timer.Live.Should().BeEmpty();
    }

    [Fact]
    public void ARefusalOfTheRetryIsRetriedAgainWhileTheDeadlineStandsAndEveryRetryHasOneTimerAtMost()
    {
        EnterWithFiles();
        _session.Handle(Position(Source, 10, 10));
        _session.Handle(Notify(0));
        _session.Handle(Notify(0));

        _timer.Live.Count(h => h.Delay == RetryDelay).Should().Be(1, "two refusals in a row must not arm two retries");

        _timer.Fire(RetryDelay);
        _session.Handle(Notify(0));
        _timer.Fire(RetryDelay);

        _x.Conversions.Select(c => c.Property).Should().Equal(FirstProperty, SecondProperty, FirstProperty);
    }

    [Fact]
    public void WhenTheDeadlineFiresAfterARefusalTheRetryIsCancelledTheStatusRefusesAndTheNextPositionAsksAgain()
    {
        EnterWithFiles();
        _session.Handle(Position(Source, 10, 10));
        _session.Handle(Notify(0));
        var retry = _timer.Live.Single(h => h.Delay == RetryDelay);

        _timer.Fire(DataTimeout);

        retry.Disposed.Should().BeTrue();
        OnlyStatus().Should().Be(new SentClientMessage(Source, _atoms.XdndStatus, Target, 2, 0, 0, 0));
        _session.Phase.Should().Be(XdndPhase.Entered);
        _log.Entries.Should().Contain(e => e.Level == LogLevel.Debug);

        _session.Handle(Position(Source, 12, 12));

        _x.Conversions.Should().HaveCount(2);
        _timer.Live.Select(h => h.Delay).Should().Equal(DataTimeout);
        _session.Phase.Should().Be(XdndPhase.FetchingData);
    }

    [Fact]
    public void ADeadlineThatFiresWithNoAnswerAtAllRefusesAndLetsTheNextPositionAskAgain()
    {
        EnterWithFiles();
        _session.Handle(Position(Source, 10, 10));

        _timer.Fire(DataTimeout);

        OnlyStatus().L1.Should().Be(2);
        _session.Phase.Should().Be(XdndPhase.Entered);
        _timer.Live.Should().BeEmpty();

        _session.Handle(Position(Source, 11, 11));

        _x.Conversions.Should().HaveCount(2);
        _timer.Live.Should().ContainSingle();
    }

    [Fact]
    public void ARefusalIsLoggedAtDebugOncePerDrag()
    {
        EnterWithFiles();
        _session.Handle(Position(Source, 10, 10));
        _session.Handle(Notify(0));
        _timer.Fire(DataTimeout);
        _session.Handle(Position(Source, 11, 11));
        _timer.Fire(DataTimeout);

        _log.Entries.Count(e => e.Level == LogLevel.Debug && e.Message.Contains("text/uri-list")).Should().Be(1);
    }

    [Fact]
    public void ANotifyWhoseValueHasVanishedIsTreatedAsARefusal()
    {
        EnterWithFiles();
        _session.Handle(Position(Source, 10, 10));

        _session.Handle(Notify(FirstProperty));

        _sink.Calls.Should().BeEmpty();
        _timer.Live.Select(h => h.Delay).Should().BeEquivalentTo([DataTimeout, RetryDelay]);
    }

    // INCR

    private void StartIncr()
    {
        EnterWithFiles();
        _session.Handle(Position(Source, 10, 10));
        _x.SetProperty(Target, FirstProperty, new XProperty(_atoms.Incr, 32, BitConverter.GetBytes((long)200_000)));
        _session.Handle(Notify(FirstProperty));
    }

    private void Chunk(string text)
    {
        _x.SetProperty(Target, FirstProperty, new XProperty(_atoms.TextUriList, 8, System.Text.Encoding.UTF8.GetBytes(text)));
        _session.Handle(NewValue(FirstProperty));
    }

    [Fact]
    public void AnIncrTransferIsCollectedChunkByChunkAndTheSinkIsEnteredOnceAtTheEnd()
    {
        StartIncr();
        Chunk("file:///home/u/a.zip\r\nfile:///ho");
        Chunk("me/u/b.zip\r\n");
        _sink.Calls.Should().BeEmpty();
        Chunk("");

        _files.Requests.Single().Should().Equal("/home/u/a.zip", "/home/u/b.zip");
        _sink.Kinds.Should().Equal("Enter");
        _x.PropertyReads.Where(r => r.Window == Target).Should().OnlyContain(r => r.Delete && r.Property == FirstProperty)
            .And.HaveCount(4, "the INCR announcement and then three chunks, each deleted as it is read");
        OnlyStatus().L1.Should().Be(3);
        _session.Phase.Should().Be(XdndPhase.Ready);
    }

    [Fact]
    public void TheDeadlineIsRearmedForEveryChunkAndDisposedWhenTheTransferEnds()
    {
        StartIncr();
        var announced = _timer.Live.Single();
        Chunk("file:///a\r\n");
        var afterFirst = _timer.Live.Single();
        Chunk("file:///b\r\n");
        var afterSecond = _timer.Live.Single();

        afterFirst.Should().NotBeSameAs(announced);
        afterSecond.Should().NotBeSameAs(afterFirst);
        announced.Disposed.Should().BeTrue();
        afterFirst.Disposed.Should().BeTrue();
        afterSecond.Delay.Should().Be(DataTimeout);

        Chunk("");

        afterSecond.Disposed.Should().BeTrue();
        _timer.Live.Should().BeEmpty();
    }

    [Fact]
    public void AnIncrTransferThatStallsDeletesTheDataRefusesAndNeverEntersTheSink()
    {
        StartIncr();
        Chunk("file:///a\r\n");
        _x.DeletedProperties.Clear();

        _timer.Fire(DataTimeout);

        _x.DeletedProperties.Should().Equal((Target, FirstProperty));
        OnlyStatus().L1.Should().Be(2);
        _sink.Calls.Should().BeEmpty();
        _session.Phase.Should().Be(XdndPhase.Entered);
    }

    [Fact]
    public void AStalledIncrTransferDoesNotLeakItsChunksIntoTheNextAttempt()
    {
        StartIncr();
        Chunk("file:///stale\r\n");
        _timer.Fire(DataTimeout);

        _session.Handle(Position(Source, 11, 11));
        Offer(SecondProperty, "file:///fresh\r\n");
        _session.Handle(Notify(SecondProperty));

        _files.Requests.Single().Should().Equal("/fresh");
    }

    [Fact]
    public void AChunkNotificationForAnotherWindowOrAnotherPropertyOrADeletionIsIgnored()
    {
        StartIncr();
        _x.PropertyReads.Clear();

        _session.Handle(new XIncomingEvent.PropertyNotify(Target + 1, FirstProperty, 0, false));
        _session.Handle(new XIncomingEvent.PropertyNotify(Target, SecondProperty, 0, false));
        _session.Handle(new XIncomingEvent.PropertyNotify(Target, FirstProperty, 0, Deleted: true));

        _x.PropertyReads.Should().BeEmpty();
    }

    [Fact]
    public void APropertyNotifyOutsideAnIncrTransferIsIgnored()
    {
        EnterWithFiles();
        _session.Handle(Position(Source, 10, 10));
        _x.PropertyReads.Clear();

        _session.Handle(NewValue(FirstProperty));

        _x.PropertyReads.Should().BeEmpty();
    }

    // Notifies that do not belong to the request

    [Fact]
    public void ANotifyForAnotherRequestorSelectionOrTargetIsIgnored()
    {
        EnterWithFiles();
        _session.Handle(Position(Source, 10, 10));
        _x.Calls.Clear();

        _session.Handle(new XIncomingEvent.SelectionNotify(Target + 1, _atoms.XdndSelection, _atoms.TextUriList, FirstProperty, 0));
        _session.Handle(new XIncomingEvent.SelectionNotify(Target, _atoms.XdndSelection + 50, _atoms.TextUriList, FirstProperty, 0));
        _session.Handle(new XIncomingEvent.SelectionNotify(Target, _atoms.XdndSelection, _atoms.TextUriList + 50, FirstProperty, 0));

        _x.Calls.Should().BeEmpty();
        _session.Phase.Should().Be(XdndPhase.FetchingData);
    }

    [Fact]
    public void ALeaveWhileFetchingNeverEntersTheSinkAndTheLateNotifyDeletesItsProperty()
    {
        EnterWithFiles();
        _session.Handle(Position(Source, 10, 10));
        Offer(FirstProperty, "file:///a\r\n");

        _session.Handle(Leave(Source));
        _session.Handle(Notify(FirstProperty));

        _sink.Calls.Should().BeEmpty();
        _x.DeletedProperties.Should().Equal((Target, FirstProperty));
        _x.PropertyReads.Should().NotContain(r => r.Property == FirstProperty);
        _session.Phase.Should().Be(XdndPhase.Idle);
        _timer.Live.Should().BeEmpty();
    }

    [Fact]
    public void ALateNotifyFromBeforeALeaveIsDeletedWhileTheNewRequestKeepsWaiting()
    {
        EnterWithFiles();
        _session.Handle(Position(Source, 10, 10));
        _session.Handle(Leave(Source));
        EnterWithFiles();
        _session.Handle(Position(Source, 10, 10));
        _x.Conversions[1].Property.Should().Be(SecondProperty);
        Offer(FirstProperty, "file:///stale\r\n");
        Offer(SecondProperty, "file:///fresh\r\n");

        _session.Handle(Notify(FirstProperty));

        _x.DeletedProperties.Should().Equal((Target, FirstProperty));
        _session.Phase.Should().Be(XdndPhase.FetchingData);
        _sink.Calls.Should().BeEmpty();

        _session.Handle(Notify(SecondProperty));

        _files.Requests.Single().Should().Equal("/fresh");
        _sink.Kinds.Should().Equal("Enter");
    }

    [Fact]
    public void ANotifyAfterAGiveUpIsDeletedAndIgnored()
    {
        EnterWithFiles();
        _session.Handle(Position(Source, 10, 10));
        _timer.Fire(DataTimeout);
        Offer(FirstProperty, "file:///late\r\n");
        _x.DeletedProperties.Clear();

        _session.Handle(Notify(FirstProperty));

        _x.DeletedProperties.Should().Equal((Target, FirstProperty));
        _sink.Calls.Should().BeEmpty();
        _session.Phase.Should().Be(XdndPhase.Entered);
    }

    [Fact]
    public void ANotifyWithNoPropertyWhileNothingIsRequestedChangesNothing()
    {
        EnterWithFiles();

        _session.Handle(Notify(0));

        _x.Calls.Should().BeEmpty();
        _timer.Armed.Should().BeEmpty();
    }

    [Fact]
    public void ANotifyForAPropertyThatIsNotOursIsLeftAlone()
    {
        EnterWithFiles();
        _x.Calls.Clear();

        _session.Handle(Notify(9999));

        _x.Calls.Should().BeEmpty();
    }

    [Fact]
    public void ALeaveFromAnotherWindowIsIgnored()
    {
        EnterWithFiles();
        _session.Handle(Position(Source, 10, 10));

        _session.Handle(Leave(OtherSource));

        _session.Phase.Should().Be(XdndPhase.FetchingData);
    }

    // The window is still being built when the user leaves

    [Fact]
    public void ALeaveWhileTheFilesAreBeingOpenedNeverEntersTheSinkAndSendsNoStatus()
    {
        _files.Hold();
        FetchFiles();
        _session.Phase.Should().Be(XdndPhase.FetchingData);

        _session.Handle(Leave(Source));
        _files.Release();

        _sink.Calls.Should().BeEmpty();
        _x.SentMessages.Should().BeEmpty();
        _session.Phase.Should().Be(XdndPhase.Idle);
    }

    [Fact]
    public void AGiveUpWhileTheFilesAreBeingOpenedRefusesOnceAndTheLateResultIsDropped()
    {
        _files.Hold();
        FetchFiles();

        _timer.Fire(DataTimeout);
        _files.Release();

        OnlyStatus().L1.Should().Be(2);
        _sink.Calls.Should().BeEmpty();
        _session.Phase.Should().Be(XdndPhase.Entered);
    }

    [Fact]
    public void AnotherNotifyWhileTheFilesAreBeingOpenedIsStale()
    {
        _files.Hold();
        FetchFiles();
        _x.DeletedProperties.Clear();
        _x.PropertyReads.Clear();

        _session.Handle(Notify(FirstProperty));
        _files.Release();

        _x.PropertyReads.Should().BeEmpty();
        _x.DeletedProperties.Should().Equal((Target, FirstProperty));
        _sink.Kinds.Should().Equal("Enter");
    }

    [Fact]
    public void ALeaveAfterTheSinkWasEnteredTellsTheSink()
    {
        FetchFiles();

        _session.Handle(Leave(Source));

        _sink.Kinds.Should().Equal("Enter", "Leave");
        _session.Phase.Should().Be(XdndPhase.Idle);
    }

    [Fact]
    public void ASecondEnterOnceReadyLeavesTheSinkBeforeStartingOver()
    {
        FetchFiles();

        EnterWithFiles(OtherSource);

        _sink.Kinds.Should().Equal("Enter", "Leave");
        _session.Phase.Should().Be(XdndPhase.Entered);
    }
}
