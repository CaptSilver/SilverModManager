using System.Collections.Concurrent;

namespace SilverModManager.Platform.Gamepad.Tests;

internal sealed class FakeSdlEvents : ISdlEvents
{
    private readonly BlockingCollection<SdlEventView> _queue = [];

    public Exception? InitThrows { get; init; }

    public bool InitResult { get; init; } = true;

    public string LastError { get; init; } = "";

    public string Version => "3.4.0";

    public List<uint> Opened { get; } = [];

    public List<nint> Closed { get; } = [];

    public Func<nint, string> Name { get; init; } = _ => "Xbox";

    public Func<nint, GamepadType> Type { get; init; } = _ => GamepadType.Xbox;

    public void Queue(SdlEventView e) => _queue.Add(e);

    public bool Init() => InitThrows is null ? InitResult : throw InitThrows;

    public bool WaitEvent(int timeoutMs, out SdlEventView e) => _queue.TryTake(out e, Math.Min(timeoutMs, 20));

    public nint OpenGamepad(uint instanceId)
    {
        Opened.Add(instanceId);
        return (nint)(1000 + instanceId);
    }

    public void CloseGamepad(nint pad) => Closed.Add(pad);

    public GamepadType TypeOf(nint pad) => Type(pad);

    public string NameOf(nint pad) => Name(pad);
}
