using Avalonia.Input;

namespace SilverModManager.App.UI.Windows.Xdnd;

/// <summary>Maps XDND action atoms to the effects of Avalonia's drag events and back.</summary>
public sealed class XdndActions(XdndAtoms atoms)
{
    /// <summary>Copy, Move and Link map to themselves. Ask, Private, an unknown atom and no atom are all Copy: we never offer a menu and a file drop is a copy anyway.</summary>
    public DragDropEffects ToEffects(nint action)
    {
        if (action == atoms.XdndActionMove) return DragDropEffects.Move;
        if (action == atoms.XdndActionLink) return DragDropEffects.Link;
        return DragDropEffects.Copy;
    }

    /// <summary>The atom for the first of Copy, Move, Link present in <paramref name="effects"/>; 0 (None) when none is.</summary>
    public nint ToAtom(DragDropEffects effects)
    {
        if (effects.HasFlag(DragDropEffects.Copy)) return atoms.XdndActionCopy;
        if (effects.HasFlag(DragDropEffects.Move)) return atoms.XdndActionMove;
        if (effects.HasFlag(DragDropEffects.Link)) return atoms.XdndActionLink;
        return 0;
    }
}
