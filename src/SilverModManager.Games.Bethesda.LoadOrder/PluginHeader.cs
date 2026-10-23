namespace SilverModManager.Games.Bethesda.LoadOrder;

/// <summary>
/// What the load order logic needs from a plugin's header record. For Tes3 plugins
/// <see cref="RecordFlags"/> is the flag word in HEDR (where the master bit lives), not the
/// record header's flags.
/// </summary>
public sealed record PluginHeader(
    string FileName,
    IReadOnlyList<string> Masters,
    uint RecordFlags,
    float? HeaderVersion,
    uint? RecordCount,
    string? Description,
    string? Author,
    PluginFormat Format);
