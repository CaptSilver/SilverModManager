namespace SilverModManager.Games.Bethesda.LoadOrder;

/// <summary>What <see cref="PluginRecordWalker"/> reads out of a plugin, before it is resolved against the plugin's masters.</summary>
/// <param name="FormIds">The raw FormIDs of a TES4-format plugin's records, in file order; empty for Morrowind.</param>
/// <param name="NamedIds">The ids of a Morrowind plugin's records, ascending; empty for the other formats.</param>
public sealed record PluginRecords(uint[] FormIds, ulong[] NamedIds)
{
    /// <summary>A plugin that has no records, such as an OpenMW script list.</summary>
    public static PluginRecords None { get; } = new([], []);
}
