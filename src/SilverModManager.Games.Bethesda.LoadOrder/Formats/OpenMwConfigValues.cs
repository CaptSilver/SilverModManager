using System.Text;

namespace SilverModManager.Games.Bethesda.LoadOrder.Formats;

/// <summary>
/// Reads the values of an openmw.cfg the way the OpenMW launcher and engine do: a leading <c>"</c> opens a quoted
/// value, <c>&amp;</c> escapes the next character, and text after the closing quote is dropped. An unquoted value
/// runs to the end of the line.
/// </summary>
public static class OpenMwConfigValues
{
    /// <summary>The value of every <c>key=</c> line, in file order. A comment or a longer key (<c>data-local</c> for <c>data</c>) is not a match.</summary>
    public static IReadOnlyList<string> Values(string text, string key)
    {
        var values = new List<string>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').TrimStart();
            var equals = line.IndexOf('=');
            if (equals < 0 || !line.AsSpan(0, equals).TrimEnd().SequenceEqual(key)) continue;
            values.Add(Unquote(line[(equals + 1)..].TrimStart()));
        }
        return values;
    }

    /// <summary>The last <c>key=</c> value, which is the one OpenMW ends up with for a key it takes once; null when there is none.</summary>
    public static string? Last(string text, string key) => Values(text, key) is { Count: > 0 } values ? values[^1] : null;

    private static string Unquote(string value)
    {
        if (value.Length == 0 || value[0] != '"') return value.TrimEnd();

        var result = new StringBuilder();
        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '&' && i + 1 < value.Length) result.Append(value[++i]);
            else if (c == '"') break;
            else result.Append(c);
        }
        return result.ToString();
    }
}
