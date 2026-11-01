using Avalonia;
using SilverModManager.App.UI.Windows.Xdnd;

namespace SilverModManager.UI.Tests.Windows.Xdnd;

public sealed record SentClientMessage(nint Destination, nint MessageType, nint L0, nint L1, nint L2, nint L3, nint L4);
public sealed record SelectionConversion(nint Selection, nint Target, nint Property, nint Requestor, nint Time);
public sealed record PropertyRead(nint Window, nint Property, bool Delete);
public sealed record PropertyDeleted(nint Window, nint Property);
public sealed record AtomsSet(nint Window, nint Property, nint[] Atoms);

/// <summary>
/// An IXTransport for session tests. Atoms are handed out in order; every request is recorded, both per kind and in
/// one ordered <see cref="Calls"/> list; property values and FromRoot answers are scripted by the test.
/// </summary>
public sealed class FakeXTransport : IXTransport
{
    private readonly Dictionary<string, nint> _atoms = [];
    private readonly Dictionary<(nint Window, nint Property), XProperty> _properties = [];
    private nint _next = 100;

    public List<string> InternCalls { get; } = [];
    public List<object> Calls { get; } = [];
    public List<SentClientMessage> SentMessages { get; } = [];
    public List<SelectionConversion> Conversions { get; } = [];
    public List<PropertyRead> PropertyReads { get; } = [];
    public List<(nint Window, nint Property)> DeletedProperties { get; } = [];
    public List<AtomsSet> AtomsSet { get; } = [];
    public List<(nint Window, int RootX, int RootY)> FromRootCalls { get; } = [];
    public int Flushes { get; private set; }
    public bool Disposed { get; private set; }

    /// <summary>Windows FromRoot reports as gone.</summary>
    public HashSet<nint> GoneWindows { get; } = [];

    /// <summary>The FromRoot answer for a live window; identity unless a test sets it.</summary>
    public Func<nint, int, int, PixelPoint> RootToWindow { get; set; } = (_, x, y) => new PixelPoint(x, y);

    public nint RootWindow => 1;

    /// <summary>Scripts the value a later GetProperty sees; a test sets it again before each INCR chunk.</summary>
    public void SetProperty(nint window, nint property, XProperty value) => _properties[(window, property)] = value;

    public nint InternAtom(string name)
    {
        InternCalls.Add(name);
        if (_atoms.TryGetValue(name, out var atom)) return atom;
        return _atoms[name] = _next++;
    }

    public string? AtomName(nint atom) => _atoms.FirstOrDefault(pair => pair.Value == atom).Key;

    public void SetAtoms(nint window, nint property, ReadOnlySpan<nint> atoms)
    {
        var copy = atoms.ToArray();
        var call = new AtomsSet(window, property, copy);
        AtomsSet.Add(call);
        Calls.Add(call);
        var bytes = new byte[copy.Length * sizeof(long)];
        for (var i = 0; i < copy.Length; i++) BitConverter.GetBytes((long)copy[i]).CopyTo(bytes, i * sizeof(long));
        _properties[(window, property)] = new XProperty(InternAtom("ATOM"), 32, bytes);
    }

    public XProperty? GetProperty(nint window, nint property, bool delete)
    {
        var read = new PropertyRead(window, property, delete);
        PropertyReads.Add(read);
        Calls.Add(read);
        if (!_properties.TryGetValue((window, property), out var value)) return null;
        if (delete) _properties.Remove((window, property));
        return value;
    }

    public void DeleteProperty(nint window, nint property)
    {
        DeletedProperties.Add((window, property));
        Calls.Add(new PropertyDeleted(window, property));
        _properties.Remove((window, property));
    }

    public void SendClientMessage(nint destination, nint messageType, nint l0, nint l1, nint l2, nint l3, nint l4)
    {
        var call = new SentClientMessage(destination, messageType, l0, l1, l2, l3, l4);
        SentMessages.Add(call);
        Calls.Add(call);
    }

    public void ConvertSelection(nint selection, nint target, nint property, nint requestor, nint time)
    {
        var call = new SelectionConversion(selection, target, property, requestor, time);
        Conversions.Add(call);
        Calls.Add(call);
    }

    public PixelPoint? FromRoot(nint window, int rootX, int rootY)
    {
        FromRootCalls.Add((window, rootX, rootY));
        return GoneWindows.Contains(window) ? null : RootToWindow(window, rootX, rootY);
    }

    public void Flush() => Flushes++;

    public void Dispose() => Disposed = true;
}
