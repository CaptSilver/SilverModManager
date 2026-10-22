using System.Globalization;
using System.Text.RegularExpressions;

namespace NexusMods.Games.Bethesda.Loot.Database;

// Follows loot-condition-interpreter's lib.rs (expression grammar) and function/parse.rs (function arguments).

/// <summary>
/// Recursive-descent port of the interpreter's nom grammar. nom distinguishes an error, which lets
/// the caller try its next alternative, from a failure, which aborts the whole parse. Here an
/// alternative that does not match returns null with the position restored, and a failure throws.
/// </summary>
internal sealed class ConditionParser
{
    private const string InvalidPathChars = "\":*?<>|";
    private const string InvalidNonRegexPathChars = "\":*?<>|\\"; // \ is invalid so that it marks a regex
    private const string InvalidRegexPathChars = "\"<>";

    private readonly string _input;
    private int _pos;

    private ConditionParser(string input) => _input = input;

    public static ConditionExpression ParseWhole(string condition)
    {
        var parser = new ConditionParser(condition);
        var expression = parser.ParseExpression();

        if (parser._pos != condition.Length)
        {
            throw new ConditionParseException(
                $"The parser did not consume the following input: \"{Escape(condition[parser._pos..])}\"", condition);
        }

        return expression;
    }

    // An expression never fails to parse, because a compound with no terms is a valid compound, so
    // "a or" is "a" followed by an empty alternative.
    private ConditionExpression ParseExpression()
    {
        List<ConditionCompound> alternatives = [ParseCompound()];
        while (TryTag("or")) alternatives.Add(ParseCompound());
        return new ConditionExpression(alternatives);
    }

    private ConditionCompound ParseCompound()
    {
        List<ConditionTerm> terms = [];

        var term = ParseTerm();
        if (term is null) return new ConditionCompound(terms);
        terms.Add(term);

        while (true)
        {
            var before = _pos;
            if (!TryTag("and")) break;

            term = ParseTerm();
            if (term is null)
            {
                _pos = before;
                break;
            }

            terms.Add(term);
        }

        return new ConditionCompound(terms);
    }

    private ConditionTerm? ParseTerm()
    {
        var start = _pos;

        if (ParseFunction() is { } function) return new ConditionTerm.OfFunction(function, false);

        if (TryTag("not"))
        {
            if (ParseFunction() is { } negated) return new ConditionTerm.OfFunction(negated, true);
            _pos = start;
        }

        if (TryTag("(") && ParseGroupBody() is { } group) return new ConditionTerm.OfGroup(group, false);
        _pos = start;

        if (TryTag("not") && TryTag("(") && ParseGroupBody() is { } negatedGroup) return new ConditionTerm.OfGroup(negatedGroup, true);
        _pos = start;

        return null;
    }

    // After the opening bracket.
    private ConditionExpression? ParseGroupBody()
    {
        var expression = ParseExpression();
        return TryTag(")") ? expression : null;
    }

    /// <summary>Skips whitespace, matches <paramref name="tag"/>, skips whitespace. Restores the position if it is not there.</summary>
    private bool TryTag(string tag)
    {
        var start = _pos;
        SkipWhitespace();

        if (string.CompareOrdinal(_input, _pos, tag, 0, tag.Length) != 0 || _pos + tag.Length > _input.Length)
        {
            _pos = start;
            return false;
        }

        _pos += tag.Length;
        SkipWhitespace();
        return true;
    }

    private void SkipWhitespace()
    {
        while (_pos < _input.Length && _input[_pos] is ' ' or '\t' or '\r' or '\n') _pos++;
    }

    /// <summary>Matches a literal with no whitespace handling.</summary>
    private bool TryLiteral(string literal)
    {
        if (_pos + literal.Length > _input.Length || string.CompareOrdinal(_input, _pos, literal, 0, literal.Length) != 0) return false;
        _pos += literal.Length;
        return true;
    }

    /// <summary>One or more characters up to the first one in <paramref name="stop"/>, or null if there are none.</summary>
    private string? IsNot(string stop)
    {
        var end = _pos;
        while (end < _input.Length && !stop.Contains(_input[end])) end++;

        if (end == _pos) return null;

        var taken = _input[_pos..end];
        _pos = end;
        return taken;
    }

    private string? TakeWhile(Func<char, bool> predicate)
    {
        var end = _pos;
        while (end < _input.Length && predicate(_input[end])) end++;

        if (end == _pos) return null;

        var taken = _input[_pos..end];
        _pos = end;
        return taken;
    }

    // Functions, in the order the interpreter tries them. An alternative that does not match restores
    // the position before the next is tried, so file("a\b") fails as a path and is read as a regex.
    private ConditionFunction? ParseFunction()
    {
        Func<ConditionFunction?>[] alternatives =
        [
            () => Delimited("file(\"", ParseNonRegexPath, "\")", p => new ConditionFunction.FilePath(p)),
            () => Delimited("file(\"", ParseRegexPath, "\")", p => new ConditionFunction.FileRegex(p.Parent, p.Pattern)),
            () => Delimited("file_size(", ParseFileSizeArgs, ")", a => new ConditionFunction.FileSize(a.Path, a.Size)),
            () => Delimited("readable(\"", ParseNonRegexPath, "\")", p => new ConditionFunction.Readable(p)),
            () => Delimited("is_executable(\"", ParseNonRegexPath, "\")", p => new ConditionFunction.IsExecutable(p)),
            () => Delimited("active(\"", ParseNonRegexPath, "\")", p => new ConditionFunction.ActivePath(p)),
            () => Delimited("active(\"", ParseRegexFilename, "\")", p => new ConditionFunction.ActiveRegex(p)),
            () => Delimited("is_master(\"", ParseNonRegexPath, "\")", p => new ConditionFunction.IsMaster(p)),
            () => Delimited("many(\"", ParseRegexPath, "\")", p => new ConditionFunction.Many(p.Parent, p.Pattern)),
            () => Delimited("many_active(\"", ParseRegexFilename, "\")", p => new ConditionFunction.ManyActive(p)),
            () => Delimited("version(", ParseVersionArgs, ")", a => new ConditionFunction.Version(a.Path, a.Version, a.Comparator)),
            () => Delimited("product_version(", ParseVersionArgs, ")", a => new ConditionFunction.ProductVersion(a.Path, a.Version, a.Comparator)),
            () => Delimited("filename_version(", ParseFilenameVersionArgs, ")", a => new ConditionFunction.FilenameVersion(a.Parent, a.Pattern, a.Version, a.Comparator)),
            () => Delimited("checksum(", ParseChecksumArgs, ")", a => new ConditionFunction.Checksum(a.Path, a.Crc)),
            () => Delimited("description_contains(", ParseDescriptionContainsArgs, ")", a => new ConditionFunction.DescriptionContains(a.Path, a.Pattern)),
        ];

        foreach (var alternative in alternatives)
        {
            var start = _pos;
            if (alternative() is { } function) return function;
            _pos = start;
        }

        return null;
    }

    private ConditionFunction? Delimited<T>(string open, Func<T?> parseArguments, string close, Func<T, ConditionFunction> build)
        where T : struct
    {
        if (!TryLiteral(open)) return null;
        if (parseArguments() is not { } arguments) return null;
        return TryLiteral(close) ? build(arguments) : null;
    }

    private ConditionFunction? Delimited(string open, Func<string?> parseArguments, string close, Func<string, ConditionFunction> build)
    {
        if (!TryLiteral(open)) return null;
        if (parseArguments() is not { } arguments) return null;
        return TryLiteral(close) ? build(arguments) : null;
    }

    private string? ParseNonRegexPath() => IsNot(InvalidNonRegexPathChars);

    // A path whose last component is a regex, which may hold characters that are not valid in paths.
    private (string Parent, string Pattern)? ParseRegexPath()
    {
        var start = _pos;
        if (IsNot(InvalidRegexPathChars) is not { } text) return null;

        if (text.EndsWith('/'))
            throw Failure($"\"{text}\" ends in a directory separator", start);

        var slash = text.LastIndexOf('/');
        var (parent, regex) = slash < 0 ? (".", text) : (text[..slash], text[(slash + 1)..]);

        return (parent, AnchoredPattern(regex, start));
    }

    private string? ParseRegexFilename()
    {
        var start = _pos;
        return IsNot(InvalidRegexPathChars) is { } text ? AnchoredPattern(text, start) : null;
    }

    private string? ParseQuotedPath()
    {
        var start = _pos;
        if (!TryLiteral("\"")) return null;
        if (IsNot(InvalidPathChars) is not { } path || !TryLiteral("\""))
        {
            _pos = start;
            return null;
        }

        return path;
    }

    private string? ParseQuotedVersion()
    {
        var start = _pos;
        if (!TryLiteral("\"")) return null;
        if (IsNot("\"") is not { } version || !TryLiteral("\""))
        {
            _pos = start;
            return null;
        }

        return version;
    }

    private bool TryComma() => TryTag(",");

    private ComparisonOperator? ParseComparator()
    {
        (string Text, ComparisonOperator Value)[] operators =
        [
            ("==", ComparisonOperator.Equal),
            ("!=", ComparisonOperator.NotEqual),
            ("<=", ComparisonOperator.LessThanOrEqual),
            (">=", ComparisonOperator.GreaterThanOrEqual),
            ("<", ComparisonOperator.LessThan),
            (">", ComparisonOperator.GreaterThan),
        ];

        foreach (var (text, value) in operators)
        {
            if (TryLiteral(text)) return value;
        }

        return null;
    }

    private (string Path, ulong Size)? ParseFileSizeArgs()
    {
        if (ParseQuotedPath() is not { } path || !TryComma()) return null;

        var start = _pos;
        if (TakeWhile(char.IsAsciiDigit) is not { } digits) return null;
        if (!ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var size))
            throw Failure($"\"{digits}\" is too large to be a file size", start);

        return (path, size);
    }

    // Version and comparator may come in either order.
    private (string Path, string Version, ComparisonOperator Comparator)? ParseVersionArgs()
    {
        var start = _pos;

        if (ParseQuotedPath() is { } path && TryComma())
        {
            var afterComma = _pos;
            if (ParseQuotedVersion() is { } version && TryComma() && ParseComparator() is { } comparator)
                return (path, version, comparator);

            _pos = afterComma;
            if (ParseComparator() is { } comparatorFirst && TryComma() && ParseQuotedVersion() is { } versionSecond)
                return (path, versionSecond, comparatorFirst);
        }

        _pos = start;
        return null;
    }

    private (string Parent, string Pattern, string Version, ComparisonOperator Comparator)? ParseFilenameVersionArgs()
    {
        var start = _pos;

        if (TryLiteral("\"") && ParseRegexPath() is { } regexPath && TryLiteral("\"") && TryComma())
        {
            var afterComma = _pos;
            if (ParseQuotedVersion() is { } version && TryComma() && ParseComparator() is { } comparator)
                return RequireOneCaptureGroup(regexPath, version, comparator, start);

            _pos = afterComma;
            if (ParseComparator() is { } comparatorFirst && TryComma() && ParseQuotedVersion() is { } versionSecond)
                return RequireOneCaptureGroup(regexPath, versionSecond, comparatorFirst, start);
        }

        _pos = start;
        return null;
    }

    private (string Parent, string Pattern, string Version, ComparisonOperator Comparator) RequireOneCaptureGroup(
        (string Parent, string Pattern) path, string version, ComparisonOperator comparator, int start)
    {
        // Group 0 is the whole match, so one explicit group is two.
        if (new Regex(path.Pattern, RegexOptions.CultureInvariant).GetGroupNumbers().Length != 2)
            throw Failure("the regex must contain exactly one capture group, which holds the version", start);

        return (path.Parent, path.Pattern, version, comparator);
    }

    private (string Path, uint Crc)? ParseChecksumArgs()
    {
        if (ParseQuotedPath() is not { } path || !TryComma()) return null;

        var start = _pos;
        if (TakeWhile(char.IsAsciiHexDigit) is not { } digits) return null;
        if (!uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var crc))
            throw Failure($"\"{digits}\" is too large to be a checksum", start);

        return (path, crc);
    }

    private (string Path, string Pattern)? ParseDescriptionContainsArgs()
    {
        if (ParseQuotedPath() is not { } path || !TryComma()) return null;

        var start = _pos;
        if (!TryLiteral("\"")) return null;

        var patternStart = _pos;
        if (IsNot("\"") is not { } pattern || !TryLiteral("\""))
        {
            _pos = start;
            return null;
        }

        CheckRegex(pattern, pattern, patternStart);
        return (path, pattern);
    }

    private string AnchoredPattern(string regex, int errorPosition)
    {
        var anchored = $"^{regex}$";
        CheckRegex(regex, anchored, errorPosition);
        return anchored;
    }

    // Rust's regex crate is the engine in LOOT, and it refuses constructs .NET accepts.
    private void CheckRegex(string source, string pattern, int errorPosition)
    {
        if (ConditionRegex.FindUnsupportedConstruct(source) is { } construct)
            throw Failure($"the regex \"{source}\" uses {construct}, which LOOT's regex engine does not support", errorPosition);

        try
        {
            _ = new Regex(pattern, ConditionRegex.Options);
        }
        catch (ArgumentException e)
        {
            throw Failure($"the regex \"{source}\" is not valid: {e.Message}", errorPosition);
        }
    }

    private ConditionParseException Failure(string reason, int position) =>
        new($"An error was encountered while parsing the expression \"{Escape(_input[position..])}\": {reason}", _input);

    private static string Escape(string text) => text.Replace("\"", "\\\"");
}
