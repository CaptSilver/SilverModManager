namespace SilverModManager.Games.FileHashes;

public partial class Diagnostics
{
    /// <summary>
    /// The game's name as it reads after "in": "the Cyberpunk 2077", and "the Witcher 3: Wild Hunt" rather than
    /// "the The Witcher 3: Wild Hunt" for a name that already starts with the article.
    /// </summary>
    internal static string WithArticle(string gameName)
        => gameName.StartsWith("the ", StringComparison.OrdinalIgnoreCase)
            ? string.Concat("the ", gameName.AsSpan(4))
            : "the " + gameName;
}
