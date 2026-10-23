namespace NexusMods.Games.Bethesda.Loot.Metadata;

public enum MetadataLoadFailure
{
    /// <summary>The metadata file does not exist.</summary>
    PathNotFound,

    /// <summary>The text holds no YAML document at all.</summary>
    NoDocuments,

    /// <summary>The text holds more than one YAML document.</summary>
    MoreThanOneDocument,
}

/// <summary>
/// Thrown when metadata cannot be loaded for a reason other than its content being invalid.
/// Follows libloot's <c>MetadataDocumentParsingError</c>.
/// </summary>
public sealed class MetadataLoadException(MetadataLoadFailure failure, string message) : Exception(message)
{
    public MetadataLoadFailure Failure { get; } = failure;
}
