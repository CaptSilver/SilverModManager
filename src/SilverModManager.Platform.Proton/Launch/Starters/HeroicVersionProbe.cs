using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using CliWrap;
using Microsoft.Extensions.Logging;
using SilverModManager.Platform.Proton.ToolRunner.Dependencies;
using SilverModManager.Sdk;

namespace SilverModManager.Platform.Proton.Launch.Starters;

/// <summary>Reads which version of Heroic is installed, since what its launch URL can carry depends on it.</summary>
public interface IHeroicVersionProbe
{
    /// <summary>
    /// The installed version of the Flatpak or native Heroic, or null when it cannot be read. Blocks the first
    /// time for each kind; the answer, null included, is kept for the life of the process.
    /// </summary>
    Version? Read(bool flatpak);
}

/// <inheritdoc />
public sealed partial class HeroicVersionProbe(IProcessRunner runner, ILogger<HeroicVersionProbe> logger) : IHeroicVersionProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<bool, Lazy<Version?>> _answers = new();

    /// <inheritdoc />
    public Version? Read(bool flatpak) => _answers.GetOrAdd(flatpak, kind => new Lazy<Version?>(() => Probe(kind))).Value;

    // The caller is asked once per Play and may sit on a thread with a synchronization context, so the probe runs
    // on the thread pool: blocking on it cannot deadlock on its own continuations.
    private Version? Probe(bool flatpak) => Task.Run(() => ProbeAsync(flatpak)).GetAwaiter().GetResult();

    private async Task<Version?> ProbeAsync(bool flatpak)
    {
        var output = new StringBuilder();
        var command = (flatpak
                ? Cli.Wrap("flatpak").WithArguments(["info", HeroicFlatpakDependency.FlatpakId])
                : Cli.Wrap("heroic").WithArguments(["--version"]))
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(output))
            .WithValidation(CommandResultValidation.None);

        using var timeout = new CancellationTokenSource(Timeout);
        try
        {
            var result = await runner.RunAsync(command, cancellationToken: timeout.Token);
            if (result.ExitCode != 0)
            {
                logger.LogDebug("`{Command}` exited with {ExitCode}, so Heroic's version is unknown", command.ToString(), result.ExitCode);
                return null;
            }
        }
        catch (Exception e) when (e is Win32Exception or ProgramNotRunnableException or OperationCanceledException)
        {
            logger.LogDebug(e, "`{Command}` did not run, so Heroic's version is unknown", command.ToString());
            return null;
        }

        var text = output.ToString();
        var match = VersionLine().Match(text);
        if (!match.Success) match = AnyVersion().Match(text);
        if (match.Success && Version.TryParse(match.Groups[1].Value, out var version)) return version;

        logger.LogDebug("No version found in the output of `{Command}`", command.ToString());
        return null;
    }

    // flatpak info prints several numbers (the runtime's, the architecture's); only its Version line is Heroic's.
    [GeneratedRegex(@"^\s*Version:\s*v?(\d+\.\d+(?:\.\d+)?)", RegexOptions.Multiline)]
    private static partial Regex VersionLine();

    [GeneratedRegex(@"(\d+\.\d+(?:\.\d+)?)")]
    private static partial Regex AnyVersion();
}
