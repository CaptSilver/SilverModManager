using JetBrains.Annotations;

namespace NexusMods.Sdk.Thunderstore;

/// <summary>
/// A Thunderstore package, identified by its team (namespace) and package name.
/// </summary>
/// <remarks>
/// The constructor does not validate, because candidate splits of an ambiguous dependency string have to be
/// representable before a lookup decides which one exists. Use <see cref="TryCreate"/> for untrusted input.
/// </remarks>
[PublicAPI]
public readonly record struct ThunderstorePackageRef(string Namespace, string Name)
{
    /// <summary>
    /// Creates a ref when the namespace is a non-empty team name and the name uses only the characters
    /// Thunderstore allows in package names (letters, digits, underscore).
    /// </summary>
    public static bool TryCreate(string @namespace, string name, out ThunderstorePackageRef package)
    {
        package = default;
        if (!IsValidNamespace(@namespace) || !IsValidName(name)) return false;
        package = new ThunderstorePackageRef(@namespace, name);
        return true;
    }

    // Team names may contain hyphens, which is what makes dependency strings ambiguous.
    internal static bool IsValidNamespace(string value)
        => value.Length > 0 && value.All(c => IsNameChar(c) || c == '-');

    internal static bool IsValidName(string value)
        => value.Length is > 0 and <= 128 && value.All(IsNameChar);

    private static bool IsNameChar(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_';

    /// <inheritdoc/>
    public override string ToString() => $"{Namespace}-{Name}";
}
