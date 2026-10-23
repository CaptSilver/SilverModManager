namespace SilverModManager.Games.Bethesda.Loot.Metadata;

/// <summary>
/// Thrown when message text and its substitutions do not line up.
/// </summary>
public sealed class MessageFormatException(string message) : FormatException(message);
