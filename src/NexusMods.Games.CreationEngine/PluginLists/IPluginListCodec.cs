using System.Text;
using NexusMods.Games.Bethesda.LoadOrder.Formats;

namespace NexusMods.Games.CreationEngine.PluginLists;

/// <summary>What a codec may need to know about the file it is replacing.</summary>
/// <param name="Existing">The text the file has on disk now, empty when there is none. Only filled for a codec that <see cref="IPluginListCodec.RewritesExisting"/>.</param>
/// <param name="DataPlugins">The name of every plugin file in the game's Data folder, whether or not the loadout provides it.</param>
public readonly record struct PluginListTarget(string Existing, IReadOnlySet<string> DataPlugins);

/// <summary>The text of one plugin list file: how a list of plugins is written and how the game's copy is read.</summary>
public interface IPluginListCodec
{
    /// <summary>The text of the file for plugins in load order, early loaders included.</summary>
    string Render(IEnumerable<PluginListEntry> entries, PluginListTarget target);

    /// <summary>The entries of the file in file order.</summary>
    IReadOnlyList<PluginListEntry> Parse(string text);

    /// <summary>The encoding the game reads the file in.</summary>
    Encoding Encoding => new UTF8Encoding(false);

    /// <summary>
    /// Whether the file holds more than the plugin list (Morrowind.ini, openmw.cfg). The app then reads what is
    /// on disk, changes only the list and writes the rest back as it was.
    /// </summary>
    bool RewritesExisting => false;
}

/// <summary>A plugins.txt that names only the active plugins.</summary>
public sealed class PlainFormat : IPluginListCodec
{
    public string Render(IEnumerable<PluginListEntry> entries, PluginListTarget target) => PlainPluginList.Render(entries);

    public IReadOnlyList<PluginListEntry> Parse(string text) => PlainPluginList.Parse(text);
}

/// <summary>A loadorder.txt: every plugin in order. It cannot say which are active, so every parsed entry is marked active.</summary>
public sealed class LoadOrderFormat : IPluginListCodec
{
    public string Render(IEnumerable<PluginListEntry> entries, PluginListTarget target) => LoadOrderTxt.Render(entries);

    public IReadOnlyList<PluginListEntry> Parse(string text) =>
        LoadOrderTxt.Parse(text).Select(name => new PluginListEntry(name, true)).ToList();
}

/// <summary>The <c>[Game Files]</c> section of Morrowind.ini, which the game writes in Windows-1252.</summary>
public sealed class MorrowindIniFormat : IPluginListCodec
{
    // Latin-1 maps every byte to a char and back, so the rest of the ini survives a rewrite untouched; the
    // plugin names are file names from Data Files and sit in the range where Latin-1 and Windows-1252 agree.
    public Encoding Encoding => Encoding.Latin1;

    public bool RewritesExisting => true;

    public string Render(IEnumerable<PluginListEntry> entries, PluginListTarget target) =>
        MorrowindIniGameFiles.Rewrite(target.Existing, entries.Where(e => e.Active).Select(e => e.Name).ToList());

    public IReadOnlyList<PluginListEntry> Parse(string text) =>
        MorrowindIniGameFiles.Parse(text).Select(name => new PluginListEntry(name, true)).ToList();
}

/// <summary>The <c>content=</c> lines of openmw.cfg for the plugins in Data Files.</summary>
public sealed class OpenMwConfigFormat : IPluginListCodec
{
    public bool RewritesExisting => true;

    public string Render(IEnumerable<PluginListEntry> entries, PluginListTarget target) =>
        // OpenMW loads builtin.omwscripts without being told; the line for it is not one the app owns.
        OpenMwContentWriter.Rewrite(target.Existing, entries.Where(e => e.Active && !e.IsEarlyLoader).Select(e => e.Name).ToList(), target.DataPlugins);

    public IReadOnlyList<PluginListEntry> Parse(string text) =>
        OpenMwContentWriter.ContentNames(text).Select(name => new PluginListEntry(name, true)).ToList();
}
