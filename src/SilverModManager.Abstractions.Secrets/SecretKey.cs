using System.Text.RegularExpressions;

namespace SilverModManager.Abstractions.Secrets;

/// <summary>
/// Names one stored secret, e.g. <c>nexus.api-key</c>. The name doubles as the Secret Service item
/// attribute, the secrets file entry and (upper-cased) the override environment variable, so it is
/// restricted to what all three can carry.
/// </summary>
public readonly partial record struct SecretKey(string Name)
{
    private const int MaxLength = 64;
    private const string EnvironmentVariablePrefix = "SILVERMM_SECRET_";

    /// <summary>
    /// The key's name; lower-case dot-separated segments, hyphens allowed after the first segment.
    /// </summary>
    public string Name { get; } = Validate(Name);

    /// <summary>
    /// The environment variable that overrides this key, e.g. <c>SILVERMM_SECRET_NEXUS_API_KEY</c>.
    /// </summary>
    public string EnvironmentVariable
    {
        get
        {
            if (Name is null) throw new InvalidOperationException("The default SecretKey has no name.");
            return EnvironmentVariablePrefix + Name.ToUpperInvariant().Replace('.', '_').Replace('-', '_');
        }
    }

    public override string ToString() => Name ?? "";

    private static string Validate(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length is 0 or > MaxLength)
            throw new ArgumentException($"A secret key must be 1 to {MaxLength} characters, got {name.Length}.", nameof(name));
        if (!NamePattern().IsMatch(name))
            throw new ArgumentException($"'{name}' is not a valid secret key: use lower-case letters and digits in dot-separated segments, with hyphens after the first segment.", nameof(name));
        return name;
    }

    // \z, not $: "$" would let a trailing newline through.
    [GeneratedRegex(@"^[a-z0-9]+(\.[a-z0-9-]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
