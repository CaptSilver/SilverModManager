namespace NexusMods.Games.Bethesda.Loot.Metadata;

/// <summary>
/// Thrown when several message contents are given and none of them is in the default language.
/// </summary>
public sealed class MultilingualMessageContentException()
    : ArgumentException($"multilingual messages must contain a content string that uses the {MessageContent.DefaultLanguage} language code");
