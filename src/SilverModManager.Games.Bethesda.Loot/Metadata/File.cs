namespace NexusMods.Games.Bethesda.Loot.Metadata;

/// <summary>
/// A file in a game's Data folder, possibly in a subdirectory. Follows libloot's metadata/file.rs.
/// </summary>
public sealed record File
{
    private readonly EquatableArray<MessageContent> _detail;

    public File(string name) => Name = new Filename(name);

    public Filename Name { get; }

    /// <summary>The name to show in messages, formatted as CommonMark.</summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Message content to append to any message generated for this file. With more than one, one
    /// must be in the default language.
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

    public string? Constraint { get; init; }
}
