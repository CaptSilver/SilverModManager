using System.Collections.Immutable;

namespace SilverModManager.Games.FromSoft.Cleanup;

/// <summary>
/// A program and its arguments, kept as an array so nothing ever passes through a shell. Equal when the arguments
/// are, which a plain record over an array would not give.
/// </summary>
public sealed record PrivilegedCommand(ImmutableArray<string> Argv)
{
    public const string ChattrProgram = "chattr";
    public const string ClearImmutableFlag = "-i";

    /// <summary><c>chattr -i</c> on one file, which is the only privileged command the cleanup knows.</summary>
    public static PrivilegedCommand ClearImmutable(string absolutePath) => new([ChattrProgram, ClearImmutableFlag, absolutePath]);

    /// <summary>The file a <see cref="ClearImmutable"/> command acts on.</summary>
    public string TargetPath => Argv.Length == 3 ? Argv[2] : throw new InvalidOperationException("The command does not name exactly one file.");

    public bool Equals(PrivilegedCommand? other) => other is not null && Argv.AsSpan().SequenceEqual(other.Argv.AsSpan());

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var argument in Argv) hash.Add(argument);
        return hash.ToHashCode();
    }
}
