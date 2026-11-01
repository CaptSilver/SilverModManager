using Avalonia.Input;
using FluentAssertions;
using SilverModManager.App.UI.Windows.Xdnd;

namespace SilverModManager.UI.Tests.Windows.Xdnd;

/// <summary>A session wired to scripted fakes, and builders for the events a source sends it.</summary>
public abstract class XdndSessionTestBase
{
    protected const nint Target = 500;
    protected const nint Source = 600;
    protected const nint OtherSource = 601;

    protected readonly FakeXTransport _x = new();
    protected readonly RecordingLogger _log = new();
    protected readonly FakeUiTimer _timer = new();
    protected readonly RecordingSink _sink = new();
    protected readonly FakeFileDropFactory _files = new();
    protected readonly XdndAtoms _atoms;
    protected readonly XdndSession _session;

    protected XdndSessionTestBase()
    {
        _atoms = new XdndAtoms(_x);
        _session = new XdndSession(Target, _x, _atoms, new UriListParser(), _files, _sink, _timer, _log);
    }

    protected static nint Version5 => (nint)5 << 24;

    protected XIncomingEvent.ClientMessage Enter(nint source, nint flags, params nint[] types) =>
        new(Target, _atoms.XdndEnter, 32, source, flags, Slot(types, 0), Slot(types, 1), Slot(types, 2));

    private static nint Slot(nint[] types, int index) => index < types.Length ? types[index] : 0;

    protected XIncomingEvent.ClientMessage Position(nint source, int rootX, int rootY, nint time = 0, nint action = 0) =>
        new(Target, _atoms.XdndPosition, 32, source, 0, ((nint)rootX << 16) | (nint)(rootY & 0xFFFF), time,
            action == 0 ? _atoms.XdndActionCopy : action);

    protected XIncomingEvent.ClientMessage Leave(nint source) =>
        new(Target, _atoms.XdndLeave, 32, source, 0, 0, 0, 0);

    protected void EnterWithFiles(nint source = Source) => _session.Handle(Enter(source, Version5, _atoms.TextUriList));

    protected XIncomingEvent.SelectionNotify Notify(nint property) =>
        new(Target, _atoms.XdndSelection, _atoms.TextUriList, property, 0);

    protected XIncomingEvent.PropertyNotify NewValue(nint property) => new(Target, property, 0, Deleted: false);

    /// <summary>Scripts the property the source wrote, as bytes of the uri list.</summary>
    protected void Offer(nint property, string uriList) =>
        _x.SetProperty(Target, property, new XProperty(_atoms.TextUriList, 8, System.Text.Encoding.UTF8.GetBytes(uriList)));

    /// <summary>Enter with files, one Position at (300, 200), and the source answering the conversion with <paramref name="uriList"/>.</summary>
    protected void FetchFiles(string uriList = "file:///home/u/a.zip\r\nfile:///home/u/b.zip\r\n", nint source = Source)
    {
        EnterWithFiles(source);
        _session.Handle(Position(source, 300, 200));
        var property = _x.Conversions[^1].Property;
        Offer(property, uriList);
        _session.Handle(Notify(property));
    }

    protected SentClientMessage OnlyStatus()
    {
        var status = _x.SentMessages.Should().ContainSingle().Subject;
        status.MessageType.Should().Be(_atoms.XdndStatus);
        return status;
    }

    protected static XProperty LongsProperty(params nint[] values)
    {
        var bytes = new byte[values.Length * sizeof(long)];
        for (var i = 0; i < values.Length; i++) BitConverter.GetBytes((long)values[i]).CopyTo(bytes, i * sizeof(long));
        return new XProperty(4, 32, bytes);
    }
}
