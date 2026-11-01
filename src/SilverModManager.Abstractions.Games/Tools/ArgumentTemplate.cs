using System.Text.RegularExpressions;

namespace SilverModManager.Abstractions.Games.Tools;

/// <summary>
/// A tool's arguments as a list of words, each of which may hold <c>{Variable}</c> placeholders. The words are
/// fixed, so a substituted value that holds spaces stays inside its word; a line the user typed is split into
/// words first and only then becomes a template.
/// </summary>
public sealed partial record ArgumentTemplate(IReadOnlyList<string> Words)
{
    public static ArgumentTemplate Empty { get; } = new([]);

    public static ArgumentTemplate Of(params string[] words) => new(words);

    /// <summary>
    /// The words with every placeholder replaced. Names match ignoring case. Braces around anything that is not a
    /// plain name (<c>{}</c>, JSON) are left as they are.
    /// </summary>
    /// <exception cref="UnknownToolVariableException">A placeholder has no entry in <paramref name="variables"/>.</exception>
    public IReadOnlyList<string> Substitute(IReadOnlyDictionary<string, string> variables)
    {
        var lookup = new Dictionary<string, string>(variables, StringComparer.OrdinalIgnoreCase);
        return Words.Select(word => Placeholder().Replace(word, match =>
        {
            var name = match.Groups["name"].Value;
            return lookup.TryGetValue(name, out var value) ? value : throw new UnknownToolVariableException(name);
        })).ToArray();
    }

    [GeneratedRegex(@"\{(?<name>[A-Za-z][A-Za-z0-9]*)\}")]
    private static partial Regex Placeholder();
}
