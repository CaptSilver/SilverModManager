using NexusMods.Games.Bethesda.LoadOrder.Formats;

namespace NexusMods.Games.CreationEngine.PluginLists;

/// <summary>The text of one plugin list file: how a list of plugins is written and how the game's copy is read.</summary>
public interface IPluginListFormat
{
    /// <summary>The text of the file for plugins in load order, early loaders included.</summary>
    string Render(IEnumerable<PluginListEntry> entries);

    /// <summary>The entries of the file in file order.</summary>
    IReadOnlyList<PluginListEntry> Parse(string text);
}

/// <summary>A plugins.txt that names only the active plugins.</summary>
public sealed class PlainFormat : IPluginListFormat
{
    public string Render(IEnumerable<PluginListEntry> entries) => PlainPluginList.Render(entries);

    public IReadOnlyList<PluginListEntry> Parse(string text) => PlainPluginList.Parse(text);
}

/// <summary>A loadorder.txt: every plugin in order. It cannot say which are active, so every parsed entry is marked active.</summary>
public sealed class LoadOrderFormat : IPluginListFormat
{
    public string Render(IEnumerable<PluginListEntry> entries) => LoadOrderTxt.Render(entries);

    public IReadOnlyList<PluginListEntry> Parse(string text) =>
        LoadOrderTxt.Parse(text).Select(name => new PluginListEntry(name, true)).ToList();
}
