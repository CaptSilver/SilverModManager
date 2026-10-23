namespace NexusMods.Games.Bethesda.Loot.Metadata;

/// <summary>
/// Data identifying the plugin under which it is stored as dirty or clean. Follows libloot's
/// metadata/plugin_cleaning_data.rs.
/// </summary>
public sealed record PluginCleaningData(uint Crc, string CleaningUtility)
{
    private readonly EquatableArray<MessageContent> _detail;

    /// <summary>Identical To Master records found in the plugin.</summary>
    public uint ItmCount { get; init; }

    public uint DeletedReferenceCount { get; init; }

    public uint DeletedNavmeshCount { get; init; }

    /// <summary>
    /// Extra message content, such as a link to a cleaning guide. With more than one, one must be
    /// in the default language.
    /// </summary>
    /// <exception cref="MultilingualMessageContentException"/>
    public EquatableArray<MessageContent> Detail
    {
        get => _detail;
        init
        {
            MessageContent.Validate(value);
            _detail = value;
        }
    }

    public string? Condition { get; init; }
}
