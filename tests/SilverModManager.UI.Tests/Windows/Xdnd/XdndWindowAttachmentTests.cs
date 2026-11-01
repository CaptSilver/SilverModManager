using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using SilverModManager.App.UI.Windows.Xdnd;
using SilverModManager.UI.Tests.Framework;

namespace SilverModManager.UI.Tests.Windows.Xdnd;

/// <summary>What one window owns: its tap on Avalonia's handler, its session and its XdndAware property.</summary>
[Collection(HeadlessUi.Collection)]
public class XdndWindowAttachmentTests(IServiceProvider provider) : AUiTest(provider)
{
    private const nint Source = 0x900001;
    private const nint Version5 = 5 << 24;

    private readonly X11HandlerStandIn _handlers = new();
    private readonly FakeXTransport _x = new();
    private readonly RecordingLogger _log = new();
    private readonly RecordingSink _sink = new();
    private readonly FakeUiTimer _timer = new();
    private XdndAtoms? _atoms;

    private XdndAtoms Atoms => _atoms ??= new XdndAtoms(_x);

    private static nint Xid => X11HandlerStandIn.Xid;

    private XdndWindowAttachment? Create(Dictionary<nint, Delegate>? windows = null) =>
        XdndWindowAttachment.TryCreate(windows ?? _handlers.Windows, _handlers.Contract, Xid, _x, Atoms, new UriListParser(),
            new FakeFileDropFactory(), _sink, _timer, _log);

    private byte[] Enter() =>
        X11HandlerStandIn.ClientMessage(Atoms.XdndEnter, Source, Version5, Atoms.TextUriList, 0, 0);

    private byte[] Position() =>
        X11HandlerStandIn.ClientMessage(Atoms.XdndPosition, Source, 0, (300 << 16) | 200, 0, Atoms.XdndActionCopy);

    [Fact]
    public async Task Create_MarksTheWindowXdndAwareAtVersion5AndFlushes()
    {
        await OnUi(() =>
        {
            using var attachment = Create();

            attachment.Should().NotBeNull();
            _x.AtomsSet.Should().ContainSingle().Which.Should().BeEquivalentTo(new AtomsSet(Xid, Atoms.XdndAware, [5]));
            _x.Calls.OfType<object>().Select(c => c.GetType()).Should().ContainInOrder(typeof(AtomsSet), typeof(FlushCall));
        });
    }

    [Fact]
    public async Task Create_ReplacesAvaloniasHandlerWithTheTap()
    {
        await OnUi(() =>
        {
            using var attachment = Create();

            _handlers.Windows[Xid].Should().NotBeSameAs(_handlers.Original);
        });
    }

    [Fact]
    public async Task Create_WhenTheTapCannotInstallSetsNothingAndReturnsNull()
    {
        await OnUi(() =>
        {
            var attachment = Create(windows: []);

            attachment.Should().BeNull();
            _x.AtomsSet.Should().BeEmpty();
            _x.Flushes.Should().Be(0);
        });
    }

    [Fact]
    public async Task Events_ReachTheSessionOnTheDispatcherNotInsideTheTap()
    {
        await OnUi(() =>
        {
            using var attachment = Create();

            _handlers.Push(Enter());
            _handlers.Push(Position());

            _x.Conversions.Should().BeEmpty("the tap only posts; raising from inside XNextEvent's drain is not allowed");

            Dispatcher.UIThread.RunJobs();

            _x.Conversions.Should().ContainSingle().Which.Requestor.Should().Be(Xid);
        });
    }

    [Fact]
    public async Task Events_StillReachAvaloniasOwnHandler()
    {
        await OnUi(() =>
        {
            using var attachment = Create();

            _handlers.Push(Enter());

            _handlers.OriginalCalls.Should().Be(1);
        });
    }

    [Fact]
    public async Task Dispose_RestoresAvaloniasHandler()
    {
        await OnUi(() =>
        {
            var attachment = Create()!;

            attachment.Dispose();

            _handlers.Windows[Xid].Should().BeSameAs(_handlers.Original);
        });
    }

    [Fact]
    public async Task Dispose_TellsASourceThatIsWaitingForAStatusNo()
    {
        await OnUi(() =>
        {
            var attachment = Create()!;
            _handlers.Push(Enter());
            _handlers.Push(Position());
            Dispatcher.UIThread.RunJobs();
            _x.SentMessages.Should().BeEmpty("the Status is owed until the data arrives");

            attachment.Dispose();

            _x.SentMessages.Should().ContainSingle().Which.MessageType.Should().Be(Atoms.XdndStatus);
        });
    }

    [Fact]
    public async Task Dispose_RemovesXdndAwareSoNoSourceStartsADragNobodyAnswers()
    {
        await OnUi(() =>
        {
            var attachment = Create()!;

            attachment.Dispose();

            _x.DeletedProperties.Should().Contain((Xid, Atoms.XdndAware));
        });
    }

    [Fact]
    public async Task Dispose_IgnoresEventsPostedBeforeItButHandledAfter()
    {
        await OnUi(() =>
        {
            var attachment = Create()!;
            _handlers.Push(Enter());
            _handlers.Push(Position());

            attachment.Dispose();
            Dispatcher.UIThread.RunJobs();

            _x.Conversions.Should().BeEmpty();
        });
    }

    [Fact]
    public async Task Dispose_CalledTwiceDoesTheWorkOnce()
    {
        await OnUi(() =>
        {
            var attachment = Create()!;

            attachment.Dispose();
            var calls = _x.Calls.Count;
            attachment.Dispose();

            _x.Calls.Should().HaveCount(calls);
        });
    }
}
