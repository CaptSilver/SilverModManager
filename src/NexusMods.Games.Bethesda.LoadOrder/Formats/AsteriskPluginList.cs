// Follows libloadorder src/load_order/asterisk_based.rs.
namespace NexusMods.Games.Bethesda.LoadOrder.Formats;

/// <summary>
/// The Plugins.txt of Skyrim SE, Fallout 4 and later: <c>*Name.esp</c> is active, a bare name is inactive,
/// and line order is load order. The game loads its early loaders regardless, so the file normally omits them.
/// </summary>
public static class AsteriskPluginList
{
    public static string Render(IEnumerable<PluginListEntry> entries, bool writeEarlyLoaders) =>
        PluginListText.Render(entries
            .Where(e => writeEarlyLoaders || !e.IsEarlyLoader)
            .Select(e => e.Active ? "*" + e.Name : e.Name));

    public static IReadOnlyList<PluginListEntry> Parse(string text) =>
        PluginListText.Lines(text)
            .Select(l => l[0] == '*' ? new PluginListEntry(l[1..], true) : new PluginListEntry(l, false))
            .ToList();
}
