// Follows libloadorder src/load_order/textfile_based.rs (the active plugins file).
namespace SilverModManager.Games.Bethesda.LoadOrder.Formats;

/// <summary>
/// A plugins.txt that lists only the active plugins, one name per line. Order and inactive plugins live elsewhere
/// (the file modification times, or <see cref="LoadOrderTxt"/>).
/// </summary>
public static class PlainPluginList
{
    public static string Render(IEnumerable<PluginListEntry> entries) =>
        PluginListText.Render(entries.Where(e => e.Active).Select(e => e.Name));

    /// <summary>Every name in the file is active.</summary>
    public static IReadOnlyList<PluginListEntry> Parse(string text) =>
        PluginListText.Lines(text).Select(l => new PluginListEntry(l, true)).ToList();
}
