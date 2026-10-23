// Follows libloadorder src/load_order/textfile_based.rs (the load order file).
namespace NexusMods.Games.Bethesda.LoadOrder.Formats;

/// <summary>
/// The loadorder.txt of the older textfile games: every plugin, active or not, one name per line in load order.
/// </summary>
public static class LoadOrderTxt
{
    public static string Render(IEnumerable<PluginListEntry> entries) =>
        PluginListText.Render(entries.Select(e => e.Name));

    public static IReadOnlyList<string> Parse(string text) => PluginListText.Lines(text).ToList();
}
