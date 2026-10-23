using System.Text;

namespace SilverModManager.Games.Bethesda.Loot.Metadata;

/// <summary>
/// A message with localisable text. Follows libloot's metadata/message.rs.
/// </summary>
public sealed record Message
{
    private readonly EquatableArray<MessageContent> _content;

    /// <summary>A message with one content string in the default language.</summary>
    public Message(MessageType type, string content)
    {
        Type = type;
        _content = new[] { new MessageContent(content) };
    }

    /// <summary>
    /// A message with the given contents. With more than one, one must be in the default language.
    /// </summary>
    /// <exception cref="MultilingualMessageContentException"/>
    public Message(MessageType type, EquatableArray<MessageContent> content)
    {
        MessageContent.Validate(content);
        Type = type;
        _content = content;
    }

    public MessageType Type { get; init; }

    public EquatableArray<MessageContent> Content
    {
        get => _content;
        init
        {
            MessageContent.Validate(value);
            _content = value;
        }
    }

    public string? Condition { get; init; }

    /// <summary>
    /// The content to show for <paramref name="language"/>, or null if there is none to show.
    /// </summary>
    /// <seealso cref="MessageContent.Select"/>
    public MessageContent? SelectMessageContent(string language) => MessageContent.Select(Content, language);

    /// <summary>
    /// Replaces <c>{n}</c> placeholders in <paramref name="text"/> with <paramref name="subs"/>[n].
    /// Anything that is not a well-formed placeholder stays literal.
    /// </summary>
    /// <exception cref="MessageFormatException">
    /// A placeholder has no substitution, or a substitution has no placeholder.
    /// </exception>
    internal static string Format(string text, IReadOnlyList<string> subs)
    {
        var unused = new SortedSet<int>(Enumerable.Range(0, subs.Count));
        var result = new StringBuilder();
        var maybeInPlaceholder = false;

        // Rust's split_inclusive(['{', '}']): every piece but possibly the last ends in its delimiter.
        var start = 0;
        while (start < text.Length)
        {
            var end = text.AsSpan(start).IndexOfAny('{', '}');
            var piece = end < 0 ? text.AsSpan(start) : text.AsSpan(start, end + 1);
            start += piece.Length;

            if (piece.EndsWith("{"))
            {
                var prefix = piece[..^1];
                if (maybeInPlaceholder)
                {
                    if (prefix.IsEmpty)
                    {
                        result.Append(piece);
                    }
                    else
                    {
                        result.Append('{').Append(prefix);
                    }
                }
                else
                {
                    result.Append(prefix);
                    maybeInPlaceholder = true;
                }
            }
            else if (piece.EndsWith("}"))
            {
                var prefix = piece[..^1];
                if (maybeInPlaceholder)
                {
                    if (TryParseIndex(prefix, out var index))
                    {
                        if (index >= (ulong)subs.Count)
                            throw new MessageFormatException($"failed to substitute a value into message, no substitution was given for the placeholder \"{{{prefix}}}\"");

                        result.Append(subs[(int)index]);
                        unused.Remove((int)index);
                    }
                    else
                    {
                        // Not a valid placeholder, treat it as normal text.
                        result.Append('{').Append(piece);
                    }

                    maybeInPlaceholder = false;
                }
                else
                {
                    result.Append(piece);
                }
            }
            else
            {
                if (maybeInPlaceholder)
                {
                    result.Append('{');
                    maybeInPlaceholder = false;
                }

                result.Append(piece);
            }
        }

        if (maybeInPlaceholder) result.Append('{');

        if (unused.Count > 0)
        {
            var index = unused.Min;
            throw new MessageFormatException($"failed to substitute \"{subs[index]}\" into message, no placeholder {{{index}}} was found");
        }

        return result.ToString();
    }

    /// <summary>
    /// Rust's <c>usize::from_str</c>: an optional leading plus and ASCII digits, nothing else.
    /// </summary>
    private static bool TryParseIndex(ReadOnlySpan<char> digits, out ulong index)
    {
        index = 0;
        if (digits.StartsWith("+")) digits = digits[1..];
        if (digits.IsEmpty) return false;

        foreach (var c in digits)
        {
            if (c is < '0' or > '9') return false;

            var digit = (ulong)(c - '0');
            if (index > (ulong.MaxValue - digit) / 10) return false;
            index = index * 10 + digit;
        }

        return true;
    }
}
