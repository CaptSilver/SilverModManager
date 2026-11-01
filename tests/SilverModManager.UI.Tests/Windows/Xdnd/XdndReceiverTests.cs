using Avalonia.Controls;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using SilverModManager.App.UI.Windows.Xdnd;
using SilverModManager.UI.Tests.Framework;

namespace SilverModManager.UI.Tests.Windows.Xdnd;

/// <summary>The service that attaches the receiver to windows: lazy transport, opened once, off for good when it can't run.</summary>
[Collection(HeadlessUi.Collection)]
public class XdndReceiverTests(IServiceProvider provider) : AUiTest(provider)
{
    private readonly FakeXTransportFactory _factory = new();
    private readonly ScriptedX11WindowHandles _handles = new();
    private readonly RecordingLoggerOf<XdndReceiver> _log = new();
    private readonly FakeXTransport _x = new();

    private XdndReceiver Receiver(AvaloniaNativeXdnd? native = null) =>
        new(_log, new FakeUiTimer(), _factory, _handles, native ?? new AvaloniaNativeXdnd());

    private Window NewWindow() => Track(new Window { Width = 100, Height = 100 });

    private int Information => _log.Entries.Count(e => e.Level == LogLevel.Information);

    [Fact]
    public async Task Constructor_DoesNotOpenTheTransport()
    {
        await OnUi(() =>
        {
            using var receiver = Receiver();

            // XInitThreads has to come before any other libX11 call in the process, so nothing may open the display
            // before a window with an XID exists.
            _factory.Opened.Should().Be(0);
        });
    }

    [Fact]
    public async Task Attach_ToAWindowWithoutAnXidAttachesNothingAndNeverOpensTheTransport()
    {
        await OnUi(() =>
        {
            _handles.Xid = null;
            using var receiver = Receiver();

            var attachment = receiver.Attach(NewWindow());
            attachment.Dispose();

            _factory.Opened.Should().Be(0);
        });
    }

    [Fact]
    public async Task Attach_WhenTheXidAppearsOnlyOnceTheWindowOpensWaitsForOpened()
    {
        await OnUi(() =>
        {
            _handles.Xid = null;
            _factory.Transport = null;
            using var receiver = Receiver();
            var window = NewWindow();
            receiver.Attach(window);

            _handles.Xid = 0x400001;
            window.Show();

            _factory.Opened.Should().Be(1);
        });
    }

    [Fact]
    public async Task Attach_WithAnXidOpensTheTransportRightAway()
    {
        await OnUi(() =>
        {
            _handles.Xid = 0x400001;
            using var receiver = Receiver();

            receiver.Attach(NewWindow());

            _factory.Opened.Should().Be(1);
        });
    }

    [Fact]
    public async Task Attach_WithNoDisplayLogsInformationOnceAcrossWindowsAndAsksTheFactoryOnce()
    {
        await OnUi(() =>
        {
            _handles.Xid = 0x400001;
            _factory.Transport = null;
            using var receiver = Receiver();

            receiver.Attach(NewWindow());
            receiver.Attach(NewWindow());

            Information.Should().Be(1);
            _factory.Opened.Should().Be(1);
        });
    }

    [Fact]
    public async Task Attach_DisposedBeforeTheWindowOpensNeverReachesTheFactory()
    {
        await OnUi(() =>
        {
            _handles.Xid = null;
            using var receiver = Receiver();
            var window = NewWindow();
            var attachment = receiver.Attach(window);
            attachment.Dispose();

            _handles.Xid = 0x400001;
            window.Show();

            _factory.Opened.Should().Be(0);
        });
    }

    [Fact]
    public async Task Attach_ToAWindowWhoseBackendIsNotX11SetsNothingOnTheServer()
    {
        await OnUi(() =>
        {
            _handles.Xid = 0x400001;
            _factory.Transport = _x;
            using var receiver = Receiver();

            receiver.Attach(NewWindow());

            _x.AtomsSet.Should().BeEmpty("the headless platform's window has no X11 handler table to tap");
            _log.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
        });
    }

    [Fact]
    public async Task Attach_WhenAvaloniaHandlesXdndItselfStaysOffAndLogsInformationOnce()
    {
        await OnUi(() =>
        {
            _handles.Xid = 0x400001;
            _factory.Transport = _x;
            using var receiver = Receiver(new NativeXdndPresent());

            receiver.Attach(NewWindow());
            receiver.Attach(NewWindow());

            _factory.Opened.Should().Be(0);
            _log.Entries.Where(e => e.Level == LogLevel.Information).Should().ContainSingle()
                .Which.Message.Should().Contain("Avalonia handles XDND itself");
        });
    }

    [Fact]
    public async Task Dispose_ClosesTheTransport()
    {
        await OnUi(() =>
        {
            _handles.Xid = 0x400001;
            _factory.Transport = _x;
            var receiver = Receiver();
            receiver.Attach(NewWindow());

            receiver.Dispose();

            _x.Disposed.Should().BeTrue();
        });
    }

    [Fact]
    public async Task Attach_AfterDisposeDoesNothing()
    {
        await OnUi(() =>
        {
            _handles.Xid = 0x400001;
            var receiver = Receiver();
            receiver.Dispose();

            receiver.Attach(NewWindow());

            _factory.Opened.Should().Be(0);
        });
    }

    private sealed class NativeXdndPresent : AvaloniaNativeXdnd
    {
        public override bool IsPresentIn(System.Reflection.Assembly avaloniaX11) => true;
    }
}
