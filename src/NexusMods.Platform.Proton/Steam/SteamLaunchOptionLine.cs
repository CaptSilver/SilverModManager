using System.Text;
using NexusMods.Sdk;

namespace NexusMods.Platform.Proton.Steam;

/// <summary>
/// Composes, parses, replaces and removes the launcher span in a Steam launch options line.
/// </summary>
/// <remarks>
/// Steam runs the line as <c>sh -c '&lt;line&gt;'</c> after replacing <c>%command%</c> textually, so the line is
/// <c>&lt;prefix&gt; '&lt;shim&gt;' run '&lt;loadout&gt;' -- %command% &lt;tail&gt;</c>. Everything outside the span
/// belongs to the user and is kept as it was.
/// </remarks>
public static class SteamLaunchOptionLine
{
    private const string CommandToken = "%command%";
    private const string ShimFileName = "silvermod";

    /// <summary>
    /// A launch options line split around the launcher span.
    /// </summary>
    /// <param name="Prefix">Text before the span (or before <c>%command%</c> when the span is absent).</param>
    /// <param name="ShimPath">The unquoted launcher path, or null when the line has no launcher span.</param>
    /// <param name="LoadoutName">The unquoted loadout name, or null when the line has no launcher span.</param>
    /// <param name="Tail">Text after <c>%command%</c>. A line without <c>%command%</c> is entirely tail.</param>
    public sealed record Parsed(string Prefix, string? ShimPath, string? LoadoutName, string Tail)
    {
        /// <summary>
        /// Whether the line contains the launcher span.
        /// </summary>
        public bool HasSpan => ShimPath is not null;
    }

    /// <summary>
    /// The outcome of an edit: the new line, or the reason the existing line cannot be edited safely.
    /// </summary>
    public sealed record Edit(string? Line, string? Refusal)
    {
        /// <summary>
        /// Whether the edit was refused.
        /// </summary>
        public bool IsRefused => Refusal is not null;
    }

    /// <summary>
    /// Builds the launcher span for a shim and loadout, preceded by the assignment that sets the DLL overrides
    /// the game needs when there are any.
    /// </summary>
    public static string Compose(string shimPath, string loadoutName, IReadOnlyList<WineDllOverride> overrides)
    {
        var prefix = WithDllOverrides("", overrides);
        if (prefix.IsRefused) throw new ArgumentException(prefix.Refusal, nameof(overrides));
        return Join(prefix.Line!, Span(shimPath, loadoutName), "");
    }

    private static string Span(string shimPath, string loadoutName)
        => $"{ShellQuote(shimPath)} run {ShellQuote(loadoutName)} -- {CommandToken}";

    /// <summary>
    /// Splits a line around its launcher span without judging whether it is safe to edit.
    /// </summary>
    public static Parsed Parse(string line)
    {
        var scan = Scan(line);
        return scan.ToParsed(line);
    }

    /// <summary>
    /// Puts the launcher span for the loadout into the line, replacing an existing span or taking the place of
    /// <c>%command%</c>, and merges <paramref name="overrides"/> into the prefix. Refuses when the user's own text
    /// would stop the shim from being the command that runs, or holds a DLL overrides value that cannot be
    /// rewritten safely.
    /// </summary>
    public static Edit Rewrite(string existingLine, string shimPath, string loadoutName, IReadOnlyList<WineDllOverride> overrides)
    {
        var scan = Scan(existingLine);
        if (Refuse(scan) is { } refusal) return new Edit(null, refusal);

        var parsed = scan.ToParsed(existingLine);
        var prefix = WithDllOverrides(parsed.Prefix, overrides);
        if (prefix.IsRefused) return prefix;

        return new Edit(Join(prefix.Line!, Span(shimPath, loadoutName), parsed.Tail), null);
    }

    /// <summary>
    /// Merges the overrides into every DLL overrides assignment in the prefix, leaving a word that already holds
    /// them as it was. With none in the prefix, one is added after the leading assignments: sh only reads a
    /// <c>KEY=value</c> word as an assignment there, and after a wrapper it would be an argument to the wrapper.
    /// The value is always double-quoted, since an unquoted ';' is an operator.
    /// </summary>
    private static Edit WithDllOverrides(string prefix, IReadOnlyList<WineDllOverride> overrides)
    {
        if (overrides.Count == 0) return new Edit(prefix, null);

        var words = new List<Word>();
        Tokenize(prefix, words, []);

        var sb = new StringBuilder();
        var copied = 0;
        var found = false;
        foreach (var word in words)
        {
            // Env names are case-sensitive, so this is not WineParser's case-insensitive lookup.
            if (EnvKey(word.Raw) != WineParser.WineDllOverridesEnvironmentVariableName) continue;
            found = true;

            var current = word.Value[(word.Value.IndexOf('=') + 1)..];
            var merged = WineParser.MergeDllOverrides(current, overrides);
            if (merged == current) continue;
            if (merged.IndexOfAny(UnquotableCharacters) >= 0 || merged.Any(char.IsWhiteSpace))
            {
                return new Edit(null, $"The launch options set {WineParser.WineDllOverridesEnvironmentVariableName} to a value with " +
                                      $"characters the app will not rewrite ({merged}). Fix it in Steam's Properties, or paste the line yourself.");
            }

            sb.Append(prefix, copied, word.Start - copied).Append(Assignment(merged));
            copied = word.End;
        }

        if (found) return new Edit(sb.Append(prefix, copied, prefix.Length - copied).ToString(), null);

        var value = WineParser.MergeDllOverrides("", overrides);
        if (value.IndexOfAny(UnquotableCharacters) >= 0 || value.Any(char.IsWhiteSpace))
        {
            return new Edit(null, $"The DLL overrides ({value}) contain characters that cannot be double-quoted in a launch option.");
        }

        var leadingAssignmentsEnd = 0;
        foreach (var word in words)
        {
            if (EnvKey(word.Raw) is null) break;
            leadingAssignmentsEnd = word.End;
        }

        var before = prefix[..leadingAssignmentsEnd].Trim();
        var after = prefix[leadingAssignmentsEnd..].Trim();
        return new Edit(Join(before, Assignment(value), after), null);
    }

    private static readonly char[] UnquotableCharacters = ['"', '`', '$', '\\'];

    private static string Assignment(string value)
        => $"{WineParser.WineDllOverridesEnvironmentVariableName}=\"{value}\"";

    /// <summary>
    /// Takes the launcher span out of the line. The user's prefix keeps its <c>%command%</c>; a line that was
    /// only our span plus a tail goes back to just the tail. A line without the span is returned unchanged.
    /// </summary>
    public static string Remove(string existingLine)
    {
        var scan = Scan(existingLine);
        if (scan.SpanStart < 0 || scan.Shim is null) return existingLine;

        var parsed = scan.ToParsed(existingLine);
        return parsed.Prefix.Length == 0
            ? parsed.Tail
            : Join(parsed.Prefix, CommandToken, parsed.Tail);
    }

    /// <summary>
    /// Replaces the value of every <c>KEY=value</c> word in the part of the line that runs before the game with
    /// <c>KEY=...</c>, keeping the rest byte for byte. Environment prefixes are where people put tokens and
    /// keys; the launcher span and everything after <c>%command%</c> are left alone. A line without
    /// <c>%command%</c> has no such part, so only its leading assignments count.
    /// </summary>
    public static string RedactEnvPrefixValues(string line)
    {
        var words = new List<Word>();
        Tokenize(line, words, []);
        var scan = Scan(line);
        var hasCommand = scan.SpanEnd > scan.SpanStart;

        var sb = new StringBuilder();
        var copied = 0;
        foreach (var word in words)
        {
            var key = EnvKey(word.Raw);
            if (hasCommand ? word.Start >= scan.SpanStart : key is null) break;
            if (key is null) continue;

            sb.Append(line, copied, word.Start - copied).Append(key).Append("=...");
            copied = word.End;
        }

        return sb.Append(line, copied, line.Length - copied).ToString();
    }

    /// <summary>The variable name when the word is a shell assignment, otherwise null.</summary>
    private static string? EnvKey(string raw)
    {
        var equals = raw.IndexOf('=');
        if (equals <= 0 || !(char.IsAsciiLetter(raw[0]) || raw[0] == '_')) return null;
        return raw.AsSpan(0, equals).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_") < 0
            ? raw[..equals]
            : null;
    }

    private static string Join(string prefix, string middle, string tail)
    {
        var sb = new StringBuilder();
        if (prefix.Length > 0) sb.Append(prefix).Append(' ');
        sb.Append(middle);
        if (tail.Length > 0) sb.Append(' ').Append(tail);
        return sb.ToString();
    }

    /// <summary>
    /// POSIX single-quotes a value for a shell command line. An embedded quote becomes <c>'\''</c>, and a
    /// literal <c>%command%</c> is split so Steam's textual substitution cannot hit it; the shell joins the
    /// pieces back.
    /// </summary>
    public static string ShellQuote(string value)
        => "'" + value.Replace("'", @"'\''").Replace(CommandToken, "%comm''and%") + "'";

    private static string? Refuse(LineScan scan)
    {
        foreach (var op in scan.Operators)
        {
            if (op.Index < scan.SpanStart)
            {
                return $"The launch options have an unquoted '{op.Text}' before %command%. Anything after it would " +
                       "not run the launcher (a '#' starts a comment, the other operators start another command), " +
                       "so Steam would keep starting the game the old way. Remove it, or paste the line yourself.";
            }

            // The rest of the line is a comment, so nothing after it runs.
            if (op.Index >= scan.SpanEnd && op.Text == "#") break;

            if (op.Index >= scan.SpanEnd && op.Text is ";" or "&" or "&&" or "|" or "||")
            {
                return $"The launch options have '{op.Text}' after %command%. That turns the line into several " +
                       "commands, the shell stays alive to run them, and Steam's Stop would hit the shell instead " +
                       "of the launcher. Remove it, or paste the line yourself.";
            }
        }

        return null;
    }

    private readonly record struct Word(int Start, int End, string Raw, string Value);

    private readonly record struct Operator(int Index, string Text);

    private sealed class LineScan
    {
        public int SpanStart { get; init; }
        public int SpanEnd { get; init; }
        public string? Shim { get; init; }
        public string? Loadout { get; init; }
        public required List<Operator> Operators { get; init; }

        public Parsed ToParsed(string line)
        {
            var prefix = line[..SpanStart].Trim();
            var tail = line[SpanEnd..].Trim();
            return new Parsed(prefix, Shim, Loadout, tail);
        }
    }

    /// <summary>
    /// Tokenizes the line the way sh would for quoting, recording unquoted operators and finding the span (or
    /// the bare <c>%command%</c>). Without <c>%command%</c> the span is empty and sits at the front, so the whole
    /// line is tail.
    /// </summary>
    private static LineScan Scan(string line)
    {
        var words = new List<Word>();
        var operators = new List<Operator>();
        Tokenize(line, words, operators);

        for (var i = 0; i + 4 < words.Count; i++)
        {
            var shim = words[i].Value;
            if (!shim.EndsWith("/" + ShimFileName, StringComparison.Ordinal)) continue;
            if (words[i + 1].Raw != "run" || words[i + 3].Raw != "--" || words[i + 4].Raw != CommandToken) continue;

            var start = words[i].Start;
            var end = words[i + 4].End;
            if (operators.Any(o => o.Index >= start && o.Index < end)) continue;

            return new LineScan
            {
                SpanStart = start, SpanEnd = end, Shim = shim, Loadout = words[i + 2].Value, Operators = operators,
            };
        }

        foreach (var word in words)
        {
            if (word.Raw != CommandToken) continue;
            return new LineScan { SpanStart = word.Start, SpanEnd = word.End, Operators = operators };
        }

        return new LineScan { SpanStart = 0, SpanEnd = 0, Operators = operators };
    }

    private static void Tokenize(string line, List<Word> words, List<Operator> operators)
    {
        var value = new StringBuilder();
        var start = -1;
        var afterRedirect = false;

        void Flush(int end)
        {
            if (start < 0) return;
            words.Add(new Word(start, end, line[start..end], value.ToString()));
            value.Clear();
            start = -1;
        }

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (char.IsWhiteSpace(c))
            {
                Flush(i);
                afterRedirect = false;
                continue;
            }

            // sh only starts a comment at the beginning of a word, and '>&', '<&' and '>|' are redirections
            // (as in 2>&1), not the background or pipe operators.
            var isOperator = c switch
            {
                '#' => start < 0,
                '&' or '|' => !afterRedirect,
                ';' or '`' => true,
                '$' => i + 1 < line.Length && line[i + 1] == '(',
                _ => false,
            };
            afterRedirect = c is '>' or '<';

            if (isOperator)
            {
                Flush(i);
                var length = c is '&' or '|' && i + 1 < line.Length && line[i + 1] == c ? 2 : c == '$' ? 2 : 1;
                operators.Add(new Operator(i, line.Substring(i, length)));
                i += length - 1;
                continue;
            }

            if (start < 0) start = i;

            switch (c)
            {
                case '\'':
                {
                    var close = line.IndexOf('\'', i + 1);
                    if (close < 0) close = line.Length;
                    value.Append(line, i + 1, close - i - 1);
                    i = close;
                    break;
                }
                case '"':
                    i = ReadDoubleQuoted(line, i, value, operators);
                    break;
                case '\\' when i + 1 < line.Length:
                    value.Append(line[++i]);
                    break;
                default:
                    value.Append(c);
                    break;
            }
        }

        Flush(line.Length);
    }

    /// <summary>
    /// Reads a double-quoted section, returning the index of its closing quote. Command substitution is still
    /// live inside double quotes, so those are recorded as operators; a '#' or ';' there is just text.
    /// </summary>
    private static int ReadDoubleQuoted(string line, int open, StringBuilder value, List<Operator> operators)
    {
        var i = open + 1;
        for (; i < line.Length && line[i] != '"'; i++)
        {
            var c = line[i];
            if (c == '\\' && i + 1 < line.Length && line[i + 1] is '$' or '`' or '"' or '\\')
            {
                value.Append(line[++i]);
                continue;
            }

            if (c == '`') operators.Add(new Operator(i, "`"));
            else if (c == '$' && i + 1 < line.Length && line[i + 1] == '(') operators.Add(new Operator(i, "$("));
            value.Append(c);
        }

        return i;
    }
}
