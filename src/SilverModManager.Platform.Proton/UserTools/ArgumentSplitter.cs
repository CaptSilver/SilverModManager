using System.Text;

namespace NexusMods.Platform.Proton.UserTools;

/// <summary>
/// Splits the arguments line a user types into words: spaces separate, double quotes group, and nothing is
/// escaped, since Windows paths are full of backslashes.
/// </summary>
internal static class ArgumentSplitter
{
    public static IReadOnlyList<string> Split(string arguments)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var inWord = false;
        var inQuotes = false;

        foreach (var c in arguments)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                inWord = true;
            }
            else if (c == ' ' && !inQuotes)
            {
                if (inWord) words.Add(word.ToString());
                word.Clear();
                inWord = false;
            }
            else
            {
                word.Append(c);
                inWord = true;
            }
        }

        if (inWord) words.Add(word.ToString());
        return words;
    }
}
