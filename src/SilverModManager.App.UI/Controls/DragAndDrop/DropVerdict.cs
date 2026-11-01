using Avalonia.Input;

namespace SilverModManager.App.UI.Controls.DragAndDrop;

/// <summary>A drop target's answer to "what would you do with this payload?".</summary>
public abstract record DropVerdict
{
    /// <summary>Not this target's kind of payload; the event is left unhandled so an enclosing zone can take it.</summary>
    public sealed record Ignore : DropVerdict;

    /// <summary>
    /// Will do <paramref name="Text"/> ("Install 3 mods into NCR Core"). <paramref name="Effect"/> is Copy or Move and
    /// drives the cursor.
    /// </summary>
    public sealed record Accept(DragDropEffects Effect, string Text) : DropVerdict;

    /// <summary>Understood but not doable. <paramref name="Reason"/> is a plain sentence shown where an accept's text would be.</summary>
    public sealed record Refuse(string Reason) : DropVerdict;
}
