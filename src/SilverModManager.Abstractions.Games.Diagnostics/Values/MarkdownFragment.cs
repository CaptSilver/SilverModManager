using JetBrains.Annotations;

namespace SilverModManager.Abstractions.Diagnostics.Values;

/// <summary>
/// Markdown the app built itself, such as a bullet list of files or numbered steps, for a message placeholder. The
/// writer escapes every plain <see cref="string"/> value as outside text, so a placeholder that carries block-level
/// Markdown has to be this type or its list markers would show as text. The fragment is trusted: build it only from
/// literals and from names run through <c>MarkdownText</c>.
/// </summary>
[PublicAPI]
public readonly record struct MarkdownFragment(string Markdown)
{
    /// <summary>The Markdown itself, so a host that prints values with <c>ToString</c> shows the text.</summary>
    public override string ToString() => Markdown;
}
