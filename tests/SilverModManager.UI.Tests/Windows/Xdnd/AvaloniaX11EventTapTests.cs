using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using SilverModManager.App.UI.Windows.Xdnd;

namespace SilverModManager.UI.Tests.Windows.Xdnd;

/// <summary>
/// The tap against a stand-in handler table and a delegate of Avalonia's real EventHandler type, so no X server is
/// involved. Events are pushed through the installed delegate from a stack local, the way XNextEvent's loop does.
/// </summary>
public class AvaloniaX11EventTapTests
{
    private const nint Xid = 0x400001;

    private readonly AvaloniaX11Contract _contract = AvaloniaX11Contract.TryResolve(typeof(X11PlatformOptions).Assembly, out _)
        ?? throw new InvalidOperationException("Avalonia.X11 contract does not resolve");
    private readonly RecordingLogger _log = new();
    private readonly Dictionary<nint, Delegate> _windows = [];
    private readonly List<string> _calls = [];
    private readonly List<XIncomingEvent> _seen = [];
    private readonly XdndAtoms _atoms;
    private readonly XEventReader _reader;
    private readonly Delegate _original;

    private Action? _insideOriginal;

    public AvaloniaX11EventTapTests()
    {
        _atoms = new XdndAtoms(new FakeXTransport());
        _reader = new XEventReader(_atoms);
        _original = CreateOriginal();
        _windows[Xid] = _original;
    }

    /// <summary>Stands in for X11Window.OnEvent: a delegate of Avalonia's EventHandler type around a method of this test.</summary>
    private Delegate CreateOriginal() =>
        Delegate.CreateDelegate(_contract.EventHandlerType, this,
            GetType().GetMethod(nameof(Original), BindingFlags.NonPublic | BindingFlags.Instance)!.MakeGenericMethod(_contract.XEventType));

    private void Original<T>(ref T ev) where T : struct
    {
        _calls.Add("original");
        _insideOriginal?.Invoke();
    }

    private static void Push<T>(Delegate handler, byte[] raw) where T : struct
    {
        T ev = default;
        raw.CopyTo(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref ev, 1)));
        var call = (RefHandler<T>)Delegate.CreateDelegate(typeof(RefHandler<T>), handler.Target, handler.Method);
        call(ref ev);
    }

    private void Push(byte[] raw)
    {
        try
        {
            typeof(AvaloniaX11EventTapTests).GetMethod(nameof(Push), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(_contract.XEventType).Invoke(null, [_windows[Xid], raw]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
    }

    private byte[] DropMessage()
    {
        var raw = new byte[192];
        BinaryPrimitives.WriteInt32LittleEndian(raw, 33);
        BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(32), Xid);
        BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(40), _atoms.XdndDrop);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(48), 32);
        BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(56), 0x3000001);
        return raw;
    }

    private static byte[] OtherEvent()
    {
        var raw = new byte[192];
        BinaryPrimitives.WriteInt32LittleEndian(raw, 6);
        return raw;
    }

    private AvaloniaX11EventTap Install(Action<XIncomingEvent> observer) =>
        AvaloniaX11EventTap.TryInstall(_contract, _windows, Xid, _reader, observer, _log)
        ?? throw new InvalidOperationException("tap did not install");

    [Fact]
    public void EventsReachTheObserverBeforeTheOriginal()
    {
        using var tap = Install(e => { _calls.Add("observer"); _seen.Add(e); });

        Push(DropMessage());

        _calls.Should().Equal("observer", "original");
        _seen.Should().ContainSingle().Which.Should().Be(new XIncomingEvent.ClientMessage(Xid, _atoms.XdndDrop, 32, 0x3000001, 0, 0, 0, 0));
    }

    [Fact]
    public void EventsThatAreNotXdndTrafficOnlyReachTheOriginal()
    {
        using var tap = Install(_ => _calls.Add("observer"));

        Push(OtherEvent());

        _calls.Should().Equal("original");
    }

    [Fact]
    public void TheInstalledHandlerReplacesTheEntryWithAnotherDelegateOfTheSameType()
    {
        using var tap = Install(_ => { });

        _windows[Xid].Should().NotBeSameAs(_original);
        _windows[Xid].GetType().Should().Be(_contract.EventHandlerType);
    }

    [Fact]
    public void AnObserverThatThrowsStillLetsTheOriginalRunAndNothingPropagates()
    {
        using var tap = Install(_ => throw new InvalidOperationException("boom"));

        var act = () => Push(DropMessage());

        act.Should().NotThrow();
        _calls.Should().Equal("original");
    }

    [Fact]
    public void AThrowingObserverIsLoggedOnceAsAWarning()
    {
        using var tap = Install(_ => throw new InvalidOperationException("boom"));

        Push(DropMessage());
        Push(DropMessage());

        _log.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
        _calls.Should().Equal("original", "original");
    }

    [Fact]
    public void ExceptionsFromTheOriginalPropagateAsWithoutTheTap()
    {
        _insideOriginal = () => throw new InvalidOperationException("from avalonia");
        using var tap = Install(_ => { });

        var act = () => Push(DropMessage());

        act.Should().Throw<InvalidOperationException>().WithMessage("from avalonia");
    }

    [Fact]
    public void DisposeRestoresTheOriginalHandler()
    {
        var tap = Install(_ => { });

        tap.Dispose();

        _windows[Xid].Should().BeSameAs(_original);
    }

    [Fact]
    public void DisposeFromInsideTheOriginalRestoresWithoutError()
    {
        var tap = Install(_ => { });
        _insideOriginal = () => tap.Dispose();

        var act = () => Push(DropMessage());

        act.Should().NotThrow();
        _windows[Xid].Should().BeSameAs(_original);
    }

    [Fact]
    public void DisposeAfterAvaloniaRemovedTheEntryAddsNothing()
    {
        var tap = Install(_ => { });
        _windows.Remove(Xid);

        var act = () => tap.Dispose();

        act.Should().NotThrow();
        _windows.Should().BeEmpty();
    }

    [Fact]
    public void DisposeLeavesAnEntryThatIsNoLongerOursAlone()
    {
        var tap = Install(_ => { });
        var someoneElses = CreateOriginal();
        _windows[Xid] = someoneElses;

        tap.Dispose();

        _windows[Xid].Should().BeSameAs(someoneElses);
    }

    [Fact]
    public void DisposeTwiceRestoresOnlyOnce()
    {
        var tap = Install(_ => { });
        tap.Dispose();
        var later = CreateOriginal();
        _windows[Xid] = later;

        tap.Dispose();

        _windows[Xid].Should().BeSameAs(later);
    }

    [Fact]
    public void AWindowWithNoEntryGetsNoTap()
    {
        _windows.Clear();

        AvaloniaX11EventTap.TryInstall(_contract, _windows, Xid, _reader, _ => { }, _log).Should().BeNull();
    }
}

/// <summary>The public entry point, which starts from a real TopLevel; the headless platform's window is not an X11Window.</summary>
public class AvaloniaX11EventTapWindowTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public async Task AHeadlessWindowGetsNoTap()
    {
        await OnUi(() =>
        {
            var window = Track(new Window());
            var reader = new XEventReader(new XdndAtoms(new FakeXTransport()));

            AvaloniaX11EventTap.TryInstall(window, 0x400001, reader, _ => { }, new RecordingLogger()).Should().BeNull();
        });
    }
}
