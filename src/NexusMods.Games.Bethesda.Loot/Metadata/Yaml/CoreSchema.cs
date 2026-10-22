using System.Numerics;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace NexusMods.Games.Bethesda.Loot.Metadata.Yaml;

/// <summary>
/// What a scalar resolves to under the YAML 1.2 core schema, as far as libloot's typed getters care.
/// </summary>
internal enum ScalarKind
{
    String,
    Null,
    Boolean,
    Integer,
    Float,
}

/// <summary>
/// Resolves scalars the way saphyr does when libloot loads metadata (saphyr's <c>Scalar::parse_from_cow</c>),
/// because YamlDotNet's representation model hands back every scalar as text.
/// </summary>
/// <remarks>
/// libloot reads <c>crc: 0x12AB</c> as an integer and rejects <c>name: 123</c> as "not a string", so
/// both the integer and the string getters go through here and cannot disagree.
/// </remarks>
internal static partial class CoreSchema
{
    [GeneratedRegex("^[+-]?(?:[0-9]+\\.?[0-9]*|\\.[0-9]+)(?:[eE][+-]?[0-9]+)?$")]
    private static partial Regex FloatPattern();

    /// <summary>
    /// Classifies <paramref name="node"/>. <paramref name="integer"/> is set for <see cref="ScalarKind.Integer"/>.
    /// </summary>
    public static ScalarKind Classify(YamlScalarNode node, out long integer)
    {
        integer = 0;

        // Any quoted, literal or folded scalar is a string.
        if (node.Style != ScalarStyle.Plain) return ScalarKind.String;

        var value = node.Value ?? "";
        if (value.Length == 0) return ScalarKind.Null;

        if (value.StartsWith("0x", StringComparison.Ordinal))
        {
            if (TryParseInteger(value.AsSpan(2), 16, out integer)) return ScalarKind.Integer;
        }
        else if (value.StartsWith("0o", StringComparison.Ordinal))
        {
            if (TryParseInteger(value.AsSpan(2), 8, out integer)) return ScalarKind.Integer;
        }
        else if (value[0] == '+')
        {
            if (TryParseInteger(value.AsSpan(1), 10, out integer)) return ScalarKind.Integer;
        }

        switch (value)
        {
            case "~" or "null" or "NULL":
                return ScalarKind.Null;
            case "true" or "false":
                return ScalarKind.Boolean;
        }

        if (TryParseInteger(value, 10, out integer)) return ScalarKind.Integer;
        return IsFloat(value) ? ScalarKind.Float : ScalarKind.String;
    }

    /// <summary>
    /// The string a getter wants, or null if the node is not a string scalar.
    /// </summary>
    public static string? AsString(YamlNode node)
    {
        return node is YamlScalarNode scalar && Classify(scalar, out _) == ScalarKind.String
            ? scalar.Value ?? ""
            : null;
    }

    /// <summary>
    /// Rust's <c>i64::from_str_radix</c>: an optional sign, then at least one digit, in range.
    /// </summary>
    private static bool TryParseInteger(ReadOnlySpan<char> text, int radix, out long value)
    {
        value = 0;

        var negative = false;
        if (text.Length > 0 && text[0] is '+' or '-')
        {
            negative = text[0] == '-';
            text = text[1..];
        }

        if (text.IsEmpty) return false;

        var accumulated = BigInteger.Zero;
        foreach (var c in text)
        {
            var digit = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => int.MaxValue,
            };
            if (digit >= radix) return false;

            accumulated = accumulated * radix + digit;
            if (accumulated > (BigInteger)long.MaxValue + 1) return false;
        }

        if (negative) accumulated = -accumulated;
        if (accumulated < long.MinValue || accumulated > long.MaxValue) return false;

        value = (long)accumulated;
        return true;
    }

    /// <summary>
    /// saphyr's <c>parse_core_schema_fp</c>: the core schema infinities and NaN, or a decimal
    /// float, which must contain a digit so that text like <c>inf</c> stays a string.
    /// </summary>
    private static bool IsFloat(string value)
    {
        switch (value)
        {
            case ".inf" or ".Inf" or ".INF" or "+.inf" or "+.Inf" or "+.INF":
            case "-.inf" or "-.Inf" or "-.INF":
            case ".nan" or ".NaN" or ".NAN":
                return true;
        }

        return FloatPattern().IsMatch(value);
    }
}
