using Avalonia;
using SilverModManager.App.UI.Windows.Xdnd;

namespace SilverModManager.UI.Tests.Windows.Xdnd;

/// <summary>An IXTransport that hands out atoms and records the names asked for. Requests beyond atom interning throw, so a test that needs them says so.</summary>
public sealed class FakeXTransport : IXTransport
{
    private readonly Dictionary<string, nint> _atoms = [];
    private nint _next = 100;

    public List<string> InternCalls { get; } = [];

    public nint RootWindow => 1;

    public nint InternAtom(string name)
    {
        InternCalls.Add(name);
        if (_atoms.TryGetValue(name, out var atom)) return atom;
        return _atoms[name] = _next++;
    }

    public string? AtomName(nint atom) => _atoms.FirstOrDefault(pair => pair.Value == atom).Key;

    public void SetAtoms(nint window, nint property, ReadOnlySpan<nint> atoms) => throw new NotSupportedException();
    public XProperty? GetProperty(nint window, nint property, bool delete) => throw new NotSupportedException();
    public void DeleteProperty(nint window, nint property) => throw new NotSupportedException();
    public void SendClientMessage(nint destination, nint messageType, nint l0, nint l1, nint l2, nint l3, nint l4) => throw new NotSupportedException();
    public void ConvertSelection(nint selection, nint target, nint property, nint requestor, nint time) => throw new NotSupportedException();
    public PixelPoint? FromRoot(nint window, int rootX, int rootY) => throw new NotSupportedException();
    public void Flush() { }
    public void Dispose() { }
}
