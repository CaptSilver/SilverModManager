using SilverModManager.Platform.Gamepad;

namespace SilverModManager.App.UI.Input;

/// <summary>One entry of the hint bar: the button's label on the connected pad and what it does now.</summary>
public sealed record HintItem(string Glyph, string Label);

public static class HintBarItems
{
    /// <summary>
    /// The bindings that would run right now, one per button: the first that can execute, as the navigator would pick it.
    /// The four directions are one entry.
    /// </summary>
    public static IReadOnlyList<HintItem> Build(IEnumerable<IPadBinding> bindings, FocusContext context, GamepadType padType)
    {
        var items = new List<HintItem>();
        var seen = new HashSet<string>();
        foreach (var binding in bindings)
        {
            if (!binding.CanExecute(context)) continue;

            var glyph = GamepadGlyphs.For(padType, binding.Button);
            if (!seen.Add(glyph)) continue;
            items.Add(new HintItem(glyph, binding.Hint(context)));
        }

        return items;
    }
}
