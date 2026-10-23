namespace SilverModManager.Games.Bethesda.LoadOrder;

/// <summary>
/// The comparer for every plugin-name lookup. The games and libloadorder match names
/// case-insensitively without regard to locale; a culture-aware comparer would let the
/// user's locale change which names count as the same plugin.
/// </summary>
public static class PluginNameComparer
{
    public static StringComparer Instance => StringComparer.OrdinalIgnoreCase;
}
