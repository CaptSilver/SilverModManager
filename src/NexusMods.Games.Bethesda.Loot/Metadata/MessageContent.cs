namespace NexusMods.Games.Bethesda.Loot.Metadata;

/// <summary>
/// A message's text in one language. Follows libloot's metadata/message.rs.
/// </summary>
public sealed record MessageContent(string Text)
{
    /// <summary>The language assumed when none is given.</summary>
    public const string DefaultLanguage = "en";

    public MessageContent() : this("") { }

    public string Language { get; init; } = DefaultLanguage;

    /// <summary>
    /// Picks the content to show for <paramref name="language"/>, which has the form
    /// <c>lang</c> or <c>lang_COUNTRY</c>.
    /// </summary>
    /// <remarks>
    /// A single content is always returned. Otherwise: an exact language match, else the first
    /// content with the same language code, else the first English content, else null. There is
    /// no "first content" fallback (message.rs <c>select_message_content</c>).
    /// </remarks>
    public static MessageContent? Select(IReadOnlyList<MessageContent> content, string language)
    {
        if (content.Count == 0) return null;
        if (content.Count == 1) return content[0];

        var underscore = language.IndexOf('_');
        var languageCode = underscore < 0 ? null : language[..underscore];

        MessageContent? matched = null;
        MessageContent? english = null;

        foreach (var candidate in content)
        {
            if (candidate.Language == language) return candidate;

            // libloot only tracks these until the first language-code match is found.
            if (matched is not null) continue;

            if (languageCode is not null)
            {
                if (languageCode == candidate.Language) matched = candidate;
            }
            else
            {
                var candidateUnderscore = candidate.Language.IndexOf('_');
                if (candidateUnderscore >= 0 && candidate.Language[..candidateUnderscore] == language) matched = candidate;
            }

            if (candidate.Language == DefaultLanguage) english = candidate;
        }

        return matched ?? english;
    }

    /// <summary>
    /// Several contents need one in the default language so there is always something to show.
    /// </summary>
    internal static void Validate(IReadOnlyList<MessageContent> contents)
    {
        if (contents.Count <= 1) return;

        foreach (var content in contents)
        {
            if (content.Language == DefaultLanguage) return;
        }

        throw new MultilingualMessageContentException();
    }
}
