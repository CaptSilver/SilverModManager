using FluentAssertions;
using Microsoft.Extensions.Logging;
using SilverModManager.App.UI.Windows.Xdnd;

namespace SilverModManager.UI.Tests.Windows.Xdnd;

/// <summary>
/// Enter and Position handling of one window's XDND conversation, against a scripted transport. Getting the data
/// and what follows it is in <see cref="XdndSessionDataTests"/>.
/// </summary>
public class XdndSessionTests : XdndSessionTestBase
{
    [Fact]
    public void ASessionStartsIdle() => _session.Phase.Should().Be(XdndPhase.Idle);

    [Fact]
    public void EnterWithInlineTypesMovesToEnteredAndSendsNothing()
    {
        _session.Handle(Enter(Source, Version5, 77, _atoms.TextUriList, 78));

        _session.Phase.Should().Be(XdndPhase.Entered);
        _x.SentMessages.Should().BeEmpty();
        _x.Conversions.Should().BeEmpty();
    }

    [Fact]
    public void EnterWithTheTypeListBitReadsXdndTypeListFromTheSourceWithoutDeleting()
    {
        _x.SetProperty(Source, _atoms.XdndTypeList, LongsProperty(77, 78, 79, _atoms.TextUriList, 80));

        _session.Handle(Enter(Source, Version5 | 1, 77, 78, 79));
        _session.Handle(Position(Source, 10, 20));

        _x.PropertyReads.Should().ContainSingle().Which.Should().Be(new PropertyRead(Source, _atoms.XdndTypeList, false));
        _x.Conversions.Should().ContainSingle("the uri list was only in the type list property");
    }

    [Fact]
    public void EnterWhoseTypeListPropertyIsGoneStaysIdle()
    {
        _session.Handle(Enter(Source, Version5 | 1, 77, 78, 79));

        _session.Phase.Should().Be(XdndPhase.Idle);
    }

    [Fact]
    public void EnterWhoseTypeListIsNotFormat32StaysIdleAndSaysWhy()
    {
        _x.SetProperty(Source, _atoms.XdndTypeList, new XProperty(4, 8, [1, 2, 3]));

        _session.Handle(Enter(Source, Version5 | 1, 77, 78, 79));

        _session.Phase.Should().Be(XdndPhase.Idle);
        _log.Entries.Should().Contain(e => e.Level == LogLevel.Debug && e.Message.Contains("format 8"));
    }

    [Fact]
    public void PositionBeforeTheDataAsksForTheUriListOnTheFirstDataPropertyAndSendsNoStatus()
    {
        EnterWithFiles();

        _session.Handle(Position(Source, 300, 200, time: 4242));

        _x.Conversions.Should().ContainSingle().Which.Should().Be(
            new SelectionConversion(_atoms.XdndSelection, _atoms.TextUriList, _atoms.DataProperties[0], Target, 4242));
        _x.SentMessages.Should().BeEmpty("the source waits for the Status, which waits for the data");
        _session.Phase.Should().Be(XdndPhase.FetchingData);
    }

    [Fact]
    public void PositionTranslatesTheRootPointToTheWindowBeforeAnyRequest()
    {
        EnterWithFiles();

        _session.Handle(Position(Source, 300, 200));

        _x.FromRootCalls.Should().ContainSingle().Which.Should().Be((Target, 300, 200));
        _x.Calls.OfType<FromRootCall>().Single().Should().Be(new FromRootCall(Target, 300, 200));
        _x.Calls.IndexOf(_x.Calls.OfType<FromRootCall>().Single()).Should()
            .BeLessThan(_x.Calls.IndexOf(_x.Conversions.Single()));
    }

    [Fact]
    public void PositionDecodesNegativeRootCoordinatesFromTheSixteenBitFields()
    {
        EnterWithFiles();

        _session.Handle(Position(Source, -5, -7));

        _x.FromRootCalls.Should().ContainSingle().Which.Should().Be((Target, -5, -7));
    }

    [Fact]
    public void PositionWhileDataIsOutstandingIsNotAnsweredAndNothingIsRequestedTwice()
    {
        EnterWithFiles();
        _session.Handle(Position(Source, 10, 10));

        _session.Handle(Position(Source, 20, 20));

        _x.Conversions.Should().ContainSingle();
        _x.SentMessages.Should().BeEmpty();
        _session.Phase.Should().Be(XdndPhase.FetchingData);
    }

    [Fact]
    public void WithoutAUriListPositionIsRefusedAtOnceAndNothingIsRequested()
    {
        _session.Handle(Enter(Source, Version5, 77, 78, 79));

        _session.Handle(Position(Source, 10, 10));

        _x.Conversions.Should().BeEmpty();
        OnlyStatus().Should().Be(new SentClientMessage(Source, _atoms.XdndStatus, Target, 2, 0, 0, 0));
        _session.Phase.Should().Be(XdndPhase.Entered);
    }

    [Fact]
    public void EveryPositionOfADragWithoutFilesIsRefused()
    {
        _session.Handle(Enter(Source, Version5, 77));

        _session.Handle(Position(Source, 10, 10));
        _session.Handle(Position(Source, 11, 11));

        _x.SentMessages.Should().HaveCount(2);
    }

    [Fact]
    public void ADragWithoutFilesLogsTheOfferedTypeNamesAtTrace()
    {
        var plainText = _x.InternAtom("text/plain");

        _session.Handle(Enter(Source, Version5, plainText));

        _log.Entries.Should().Contain(e => e.Level == LogLevel.Trace && e.Message.Contains("text/plain"));
    }

    [Fact]
    public void ADragWithoutFilesAsksForNoTypeNamesWhenTraceIsOff()
    {
        _log.Minimum = LogLevel.Debug;
        var plainText = _x.InternAtom("text/plain");

        _session.Handle(Enter(Source, Version5, plainText));

        _x.AtomNameCalls.Should().BeEmpty("naming a type is a round trip to the X server");
        _log.Entries.Should().NotContain(e => e.Level == LogLevel.Trace);
    }

    [Fact]
    public void PositionFromAnotherWindowIsIgnoredAndLoggedAtDebug()
    {
        EnterWithFiles();

        _session.Handle(Position(OtherSource, 10, 10));

        _x.Calls.Should().BeEmpty();
        _session.Phase.Should().Be(XdndPhase.Entered);
        _log.Entries.Should().Contain(e => e.Level == LogLevel.Debug);
    }

    [Fact]
    public void PositionBeforeAnyEnterIsIgnored()
    {
        _session.Handle(Position(Source, 10, 10));

        _x.Calls.Should().BeEmpty();
        _session.Phase.Should().Be(XdndPhase.Idle);
    }

    [Fact]
    public void PositionForAWindowThatIsGoneEndsTheConversation()
    {
        EnterWithFiles();
        _x.GoneWindows.Add(Target);

        _session.Handle(Position(Source, 10, 10));

        _session.Phase.Should().Be(XdndPhase.Idle);
        _x.Conversions.Should().BeEmpty();
        _x.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public void ASecondEnterFromAnotherSourceStartsOverAndTheOldSourceIsIgnored()
    {
        EnterWithFiles();
        _session.Handle(Position(Source, 10, 10));

        _session.Handle(Enter(OtherSource, Version5, _atoms.TextUriList));
        _session.Handle(Position(Source, 12, 12));

        _session.Phase.Should().Be(XdndPhase.Entered);
        _x.Conversions.Should().ContainSingle("only the first source's position was asked for");
    }

    [Fact]
    public void ASecondEnterFromTheNewSourceThenAsksForItsOwnData()
    {
        EnterWithFiles();
        _session.Handle(Position(Source, 10, 10));
        _session.Handle(Enter(OtherSource, Version5, _atoms.TextUriList));

        _session.Handle(Position(OtherSource, 12, 12));

        _x.Conversions.Select(c => c.Property).Should().Equal(_atoms.DataProperties[0], _atoms.DataProperties[1]);
        _session.Phase.Should().Be(XdndPhase.FetchingData);
    }

    [Fact]
    public void TheThirdRequestWrapsBackToTheFirstDataProperty()
    {
        const nint thirdSource = 602;
        EnterWithFiles();
        _session.Handle(Position(Source, 10, 10));
        EnterWithFiles(OtherSource);
        _session.Handle(Position(OtherSource, 10, 10));

        EnterWithFiles(thirdSource);
        _session.Handle(Position(thirdSource, 10, 10));

        _x.Conversions.Select(c => c.Property).Should().Equal(
            _atoms.DataProperties[0], _atoms.DataProperties[1], _atoms.DataProperties[0]);
    }

    [Fact]
    public void MessagesOfOtherTypesAreIgnoredForNow()
    {
        _session.Handle(new XIncomingEvent.ClientMessage(Target, _x.InternAtom("WM_PROTOCOLS"), 32, 0, 0, 0, 0, 0));

        _x.Calls.Should().BeEmpty();
        _session.Phase.Should().Be(XdndPhase.Idle);
    }
}
