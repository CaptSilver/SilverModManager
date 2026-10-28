using System.Text;

namespace SilverModManager.Games.Witcher.Witcher3;

/// <summary>
/// The Witcher 3's <c>mods.settings</c>: one INI section per Mods folder, <c>Enabled</c> and <c>Priority</c> keys.
/// The game reads the smaller Priority first, so the winner of two folders is the one with the smaller number.
/// </summary>
public static class ModsSettingsDocument
{
    /// <summary>One section of the file. <paramref name="Priority"/> is <see cref="int.MaxValue"/> when the file gave none.</summary>
    public sealed record Section(string Folder, bool Enabled, int Priority);

    /// <summary>
    /// The file for <paramref name="order"/>, winner first: Priority is the position plus one, and a folder listed under
    /// two spellings gets the first one's section only, since the game reads folder names without regard to case.
    /// Vortex's <c>VK</c> key is not written; the game ignores it.
    /// </summary>
    public static string Render(IReadOnlyList<(string Folder, bool IsEnabled)> order)
    {
        var text = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var priority = 0;
        foreach (var (folder, isEnabled) in order)
        {
            if (!seen.Add(folder)) continue;

            if (priority > 0) text.Append("\r\n");
            text.Append('[').Append(folder).Append("]\r\n");
            text.Append("Enabled=").Append(isEnabled ? '1' : '0').Append("\r\n");
            text.Append("Priority=").Append(++priority).Append("\r\n");
        }

        return text.ToString();
    }

    /// <summary>
    /// The sections of a file in file order, however it was written: keys it does not know are ignored, a section
    /// without <c>Enabled</c> is on, one without a readable <c>Priority</c> sorts last, and duplicate priorities stay.
    /// </summary>
    public static IReadOnlyList<Section> Parse(string text)
    {
        var sections = new List<Section>();
        string? folder = null;
        var enabled = true;
        var priority = int.MaxValue;

        void Close()
        {
            if (folder is not null) sections.Add(new Section(folder, enabled, priority));
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                Close();
                folder = line[1..^1];
                enabled = true;
                priority = int.MaxValue;
                continue;
            }

            var equals = line.IndexOf('=');
            if (folder is null || equals < 0) continue;

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            if (key.Equals("Enabled", StringComparison.OrdinalIgnoreCase)) enabled = ParseEnabled(value);
            else if (key.Equals("Priority", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var parsed)) priority = parsed;
        }

        Close();
        return sections;
    }

    /// <summary>
    /// Whether <paramref name="text"/> can be the file at all: no NUL characters, and some section header if there
    /// is any text. <see cref="Parse"/> never throws, so this is what tells an unreadable file from an empty one.
    /// </summary>
    public static bool CanParse(string text)
    {
        if (text.Contains('\0')) return false;
        if (text.AsSpan().Trim().IsEmpty) return true;

        return text.Split('\n').Select(line => line.Trim()).Any(line => line.Length > 1 && line[0] == '[' && line[^1] == ']');
    }

    private static bool ParseEnabled(string value) => value switch
    {
        "0" => false,
        _ when value.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
        _ => true,
    };
}
