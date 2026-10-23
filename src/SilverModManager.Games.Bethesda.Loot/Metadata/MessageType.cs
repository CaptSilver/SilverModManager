namespace NexusMods.Games.Bethesda.Loot.Metadata;

/// <summary>
/// How severe a message is. Follows libloot's metadata/message.rs.
/// </summary>
public enum MessageType
{
    /// <summary>A notification of no significant severity.</summary>
    Say,

    /// <summary>An issue the user may want to act on.</summary>
    Warn,

    /// <summary>An issue that needs user action.</summary>
    Error,
}
