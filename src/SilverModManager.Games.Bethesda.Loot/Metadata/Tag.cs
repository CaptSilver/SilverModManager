namespace NexusMods.Games.Bethesda.Loot.Metadata;

/// <summary>
/// Whether a Bash Tag suggestion adds or removes the tag. Follows libloot's metadata/tag.rs.
/// </summary>
public enum TagSuggestion
{
    Addition,
    Removal,
}

/// <summary>
/// A Bash Tag suggestion for a plugin.
/// </summary>
public sealed record Tag(string Name, TagSuggestion Suggestion = TagSuggestion.Addition)
{
    public string? Condition { get; init; }

    public bool IsAddition => Suggestion == TagSuggestion.Addition;

    /// <summary>
    /// Splits a metadata tag string: a leading <c>-</c> means the tag should be removed.
    /// </summary>
    internal static (string Name, TagSuggestion Suggestion) NameAndSuggestion(string value)
    {
        return value.StartsWith('-')
            ? (value[1..], TagSuggestion.Removal)
            : (value, TagSuggestion.Addition);
    }
}
