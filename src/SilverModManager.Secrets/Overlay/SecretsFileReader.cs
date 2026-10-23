using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace NexusMods.Secrets.Overlay;

/// <summary>
/// Where a secrets file went wrong. <see cref="Line"/> is 1-based, or 0 when the problem is the file itself.
/// </summary>
public sealed record SecretsFileError(int Line, string Message)
{
    public override string ToString() => Line > 0 ? $"line {Line}: {Message}" : Message;
}

/// <summary>
/// The parsed secrets file: environment variable name to value, or the first error.
/// </summary>
public sealed record SecretsFileContents(IReadOnlyDictionary<string, string> Values, SecretsFileError? Error);

/// <summary>
/// Reads a secrets file in the shape of a shell env file: <c>NAME=value</c> lines, <c>#</c> comments,
/// an optional <c>export </c> prefix and optional single or double quotes around the value. Nothing
/// inside the quotes is unescaped, and an unquoted value runs to the end of the line.
/// </summary>
public sealed partial class SecretsFileReader(string path, ILogger logger)
{
    private const UnixFileMode GroupAndOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    public string Path { get; } = path;

    /// <summary>
    /// Reads the file. A file readable by group or other is read anyway with a warning, because the
    /// user chose it and may have shared it on purpose; a missing or unreadable file is an error, so a
    /// mistyped path does not quietly fall back to the keyring.
    /// </summary>
    public SecretsFileContents Read()
    {
        string[] lines;
        try
        {
            var mode = System.IO.File.GetUnixFileMode(Path);
            if ((mode & GroupAndOther) != 0)
                logger.LogWarning("Secrets file {Path} has mode {Mode} and can be read by other users; it is read anyway", Path, Convert.ToString((int)mode & 0xFFF, 8).PadLeft(3, '0'));

            lines = System.IO.File.ReadAllLines(Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new SecretsFileContents(new Dictionary<string, string>(), new SecretsFileError(0, $"Could not read the secrets file {Path}: {e.Message}"));
        }

        return Parse(lines);
    }

    public static SecretsFileContents Parse(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var number = 0;
        foreach (var raw in lines)
        {
            number++;
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            if (line.StartsWith("export", StringComparison.Ordinal) && line.Length > 6 && char.IsWhiteSpace(line[6]))
                line = line[6..].TrimStart();

            var equals = line.IndexOf('=');
            if (equals < 0) return Fail(values, number, "expected NAME=value");

            var name = line[..equals];
            if (!NamePattern().IsMatch(name)) return Fail(values, number, $"'{name}' is not a valid variable name");

            var value = line[(equals + 1)..];
            if (value.Length > 0 && value[0] is '"' or '\'')
            {
                var quote = value[0];
                if (value.Length < 2 || value[^1] != quote) return Fail(values, number, $"the value of {name} starts with {quote} but does not end with it");
                value = value[1..^1];
            }

            values[name] = value;
        }

        return new SecretsFileContents(values, null);
    }

    private static SecretsFileContents Fail(Dictionary<string, string> values, int line, string message)
        => new(values, new SecretsFileError(line, message));

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
