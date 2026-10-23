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

    /// <summary>The encoding the file is written in. Windows-1252 unless the file says otherwise.</summary>
    Encoding Encoding => PluginListEncoding.Windows1252;

    /// <summary>The text of the file's bytes as they are read back.</summary>
    string Decode(ReadOnlySpan<byte> bytes) => Encoding.GetString(bytes);

    /// <summary>
    /// Whether the file holds more than the plugin list (Morrowind.ini, openmw.cfg). The app then reads what is
    /// on disk, changes only the list and writes the rest back as it was.
    /// </summary>
    bool RewritesExisting => false;

    /// <summary>
    /// Whether an entry's active flag means anything. A file that lists every plugin regardless (loadorder.txt)
    /// marks them all active when parsed, so only its order can differ from the app's.
    /// </summary>
    bool CarriesSwitches => true;
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
    // libloadorder writes loadorder.txt as UTF-8 and reads it as UTF-8 with a Windows-1252 fallback.
    public Encoding Encoding => PluginListEncoding.StrictUtf8;

    public string Decode(ReadOnlySpan<byte> bytes) => PluginListEncoding.DecodeUtf8OrWindows1252(bytes);

    public bool CarriesSwitches => false;

    public string Render(IEnumerable<PluginListEntry> entries, PluginListTarget target) => LoadOrderTxt.Render(entries);

    public IReadOnlyList<PluginListEntry> Parse(string text) =>
        LoadOrderTxt.Parse(text).Select(name => new PluginListEntry(name, true)).ToList();
}

/// <summary>The <c>[Game Files]</c> section of Morrowind.ini, which the game writes in Windows-1252.</summary>
public sealed class MorrowindIniFormat : IPluginListCodec
{
    // libloadorder reads and writes Morrowind.ini as Windows-1252. 0x80-0x9F hold real characters there (the euro
    // sign, Š, Œ), which Latin-1 would read as control characters and write back as '?'. Every byte still
    // round-trips, so the rest of the ini survives a rewrite untouched.
    public Encoding Encoding => PluginListEncoding.Windows1252;

    public bool RewritesExisting => true;

    public string Render(IEnumerable<PluginListEntry> entries, PluginListTarget target) =>
        MorrowindIniGameFiles.Rewrite(target.Existing, entries.Where(e => e.Active).Select(e => e.Name).ToList());

    public IReadOnlyList<PluginListEntry> Parse(string text) =>
        MorrowindIniGameFiles.Parse(text).Select(name => new PluginListEntry(name, true)).ToList();
}

/// <summary>The <c>content=</c> lines of openmw.cfg for the plugins in Data Files.</summary>
public sealed class OpenMwConfigFormat : IPluginListCodec
{
    public const string FileName = "openmw.cfg";

    // openmw.cfg is UTF-8, and libloadorder refuses one that isn't rather than rewrite it with substitutes.
    public Encoding Encoding => PluginListEncoding.StrictUtf8;

    public string Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return PluginListEncoding.DecodeUtf8(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new UndecodablePluginListException(FileName, "UTF-8");
        }
    }

    public bool RewritesExisting => true;

    public string Render(IEnumerable<PluginListEntry> entries, PluginListTarget target) =>
        // OpenMW loads builtin.omwscripts without being told; the line for it is not one the app owns.
        OpenMwContentWriter.Rewrite(target.Existing, entries.Where(e => e.Active && !e.IsEarlyLoader).Select(e => e.Name).ToList(), target.DataPlugins);

    public IReadOnlyList<PluginListEntry> Parse(string text) =>
        OpenMwContentWriter.ContentNames(text).Select(name => new PluginListEntry(name, true)).ToList();
}
