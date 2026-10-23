using NexusMods.Games.FromSoft.Formats;

namespace NexusMods.Games.FromSoft.Heal;

/// <summary>What a param's shape has to agree on before rows can be transplanted between two files.</summary>
public sealed record Layout(int Rows, int Stride, int ParamdefVersion);

/// <summary>The shape of every param table in a regulation.</summary>
public static class ParamLayouts
{
    /// <summary>The tables of a decrypted regulation, read through the binder of the game with these traits.</summary>
    public static IReadOnlyDictionary<string, Layout?> Of(byte[] payload, FromSoftFormatTraits traits)
        => Of(Binders.For(traits).Read(payload));

    /// <summary>
    /// Table name to layout, null where the entry cannot be read. A few of vanilla's own entries carry a strings offset
    /// past the end of the file: FromSoft writes them that way and a tool normalises them on re-save, so a mod's copy
    /// parses where the game's does not. Recording null beats throwing, because the gate asks whether a transplant is
    /// safe and a table nobody transplants never has to be read. The merge refuses to guess when an unreadable entry
    /// differs on both sides.
    /// </summary>
    public static IReadOnlyDictionary<string, Layout?> Of(IEnumerable<BinderEntry> entries)
    {
        var layouts = new Dictionary<string, Layout?>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!entry.Name.EndsWith(".param", StringComparison.OrdinalIgnoreCase)) continue;
            var name = entry.Name[(entry.Name.LastIndexOf('\\') + 1)..];
            try
            {
                var table = Param.Read(entry.Data);
                layouts[name] = new Layout(table.Rows.Length, table.Stride, table.ParamdefDataVersion);
            }
            catch (ParamException)
            {
                layouts[name] = null;
            }
        }
        return layouts;
    }
}
