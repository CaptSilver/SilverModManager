using System.Diagnostics.CodeAnalysis;
using NexusMods.Paths;

namespace SilverModManager.Platform.Proton.Launch;

/// <summary>
/// The <c>--secrets-file</c> option, shared by <c>run</c> and the verbs that reach Nexus on their own.
/// </summary>
/// <remarks>
/// The option has to be read before any host exists, because the host's secret store is built from it, so it
/// cannot go through the verb parser that runs afterwards.
/// </remarks>
public static class SecretsFileOption
{
    public const string Name = "--secrets-file";

    /// <summary>
    /// A relative path is taken from the working directory, which a headless main started by the same command inherits.
    /// </summary>
    public static AbsolutePath ToPath(string value) => FileSystem.Shared.FromUnsanitizedFullPath(Path.GetFullPath(value));

    /// <summary>
    /// Finds the option in <paramref name="args"/>, in either the <c>--secrets-file x</c> or the <c>--secrets-file=x</c> form.
    /// Everything after a <c>--</c> is not ours and is left alone.
    /// </summary>
    /// <returns>False with an <paramref name="error"/> when the option is malformed; true with a null path when it is absent.</returns>
    public static bool TryFind(IReadOnlyList<string> args, out AbsolutePath? path, [NotNullWhen(false)] out string? error)
    {
        path = null;
        error = null;
        string? found = null;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--") break;

            string? value;
            if (arg == Name)
            {
                value = i + 1 < args.Count ? args[++i] : null;
            }
            else if (arg.StartsWith(Name + "=", StringComparison.Ordinal))
            {
                value = arg[(Name.Length + 1)..];
            }
            else continue;

            if (string.IsNullOrWhiteSpace(value))
            {
                error = $"{Name} needs a value.";
                return false;
            }

            if (found is not null)
            {
                error = $"{Name} given more than once.";
                return false;
            }
            found = value;
        }

        if (found is not null) path = ToPath(found);
        return true;
    }
}
