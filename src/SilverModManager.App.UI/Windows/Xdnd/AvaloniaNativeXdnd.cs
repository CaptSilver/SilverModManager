using System.Reflection;

namespace SilverModManager.App.UI.Windows.Xdnd;

/// <summary>
/// Whether Avalonia's X11 backend already speaks XDND. The receiver exists because 11.3.5 has no <c>XdndAware</c>
/// atom; once <c>X11Atoms</c> grows one, running our receiver beside it would put two receivers on one window, so
/// the receiver stays off. Virtual so a test can say "Avalonia has it".
/// </summary>
public class AvaloniaNativeXdnd
{
    private const string AtomsTypeName = "Avalonia.X11.X11Atoms";
    private const string AtomName = "XdndAware";
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public virtual bool IsPresentIn(Assembly avaloniaX11) => IsPresentIn(avaloniaX11.GetType(AtomsTypeName));

    /// <param name="x11Atoms">Avalonia's atom table type; null when the backend has none (then there is nothing to find).</param>
    public bool IsPresentIn(Type? x11Atoms) =>
        x11Atoms is not null && (x11Atoms.GetField(AtomName, Members) is not null || x11Atoms.GetProperty(AtomName, Members) is not null);
}
