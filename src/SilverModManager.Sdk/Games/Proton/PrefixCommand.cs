using JetBrains.Annotations;
using NexusMods.Paths;

namespace SilverModManager.Sdk.Games;

/// <summary>
/// A program to run inside a game's Wine prefix: an executable on the host, or a Wine verb such as
/// <c>msiexec</c>, <c>regedit</c> or <c>wineboot</c> that Wine finds itself.
/// </summary>
[PublicAPI]
public sealed record PrefixCommand
{
    /// <summary>
    /// The program as it goes on the command line: the executable's path, or the bare verb.
    /// </summary>
    public string Program { get; }

    /// <summary>
    /// The executable, or null when this is a Wine verb.
    /// </summary>
    public AbsolutePath? Exe { get; }

    /// <summary>
    /// The program's arguments. For an executable, host paths in them are rewritten to Windows paths. For a
    /// verb they are passed as they are, because Windows switches such as <c>/i</c> look like host paths;
    /// a verb's caller writes Windows paths itself.
    /// </summary>
    public IReadOnlyList<string> Args { get; }

    /// <summary>
    /// Variables to set on top of the ones the strategy sets, winning on a clash.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Env { get; }

    /// <summary>
    /// Whether this is a Wine verb rather than an executable.
    /// </summary>
    public bool IsVerb => Exe is null;

    private PrefixCommand(string program, AbsolutePath? exe, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env)
    {
        Program = program;
        Exe = exe;
        Args = args;
        Env = env;
    }

    /// <summary>
    /// A command that runs the executable at <paramref name="exe"/>.
    /// </summary>
    public static PrefixCommand ForExe(AbsolutePath exe, IReadOnlyList<string>? args = null, IReadOnlyDictionary<string, string>? env = null)
        => new(exe.ToString(), exe, args ?? [], env);

    /// <summary>
    /// A command that runs a Wine verb, a bare program name that Wine resolves inside the prefix.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="verb"/> is empty or is a path or contains whitespace.</exception>
    public static PrefixCommand ForVerb(string verb, IReadOnlyList<string>? args = null, IReadOnlyDictionary<string, string>? env = null)
    {
        if (verb.Length == 0 || verb.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\'))
            throw new ArgumentException($"A Wine verb is a bare program name such as 'msiexec', not '{verb}'. Use {nameof(ForExe)} to run a file.", nameof(verb));

        return new PrefixCommand(verb, null, args ?? [], env);
    }
}

/// <summary>
/// What a program run inside a prefix reported.
/// </summary>
/// <param name="ExitCode">The exit code of the program that was started.</param>
/// <param name="Stdout">Everything it wrote to standard output.</param>
/// <param name="Stderr">Everything it wrote to standard error.</param>
[PublicAPI]
public sealed record PrefixRunResult(int ExitCode, string Stdout, string Stderr);
