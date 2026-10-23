using System.ComponentModel;
using System.Text;
using CliWrap;
using NexusMods.Paths;
using SilverModManager.Sdk;

namespace SilverModManager.Games.FromSoft.Doctor;

/// <summary>Runs <c>me3 --version</c> through the process abstraction.</summary>
public class Me3VersionProbe(IProcessRunner processRunner)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// What the binary printed, or null when it could not be started, exited with an error, printed nothing or did not
    /// finish in time.
    /// </summary>
    public string? Read(AbsolutePath binary)
    {
        var output = new StringBuilder();
        var command = Cli.Wrap(binary.ToString())
            .WithArguments("--version")
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(output))
            .WithValidation(CommandResultValidation.None);

        using var cancellation = new CancellationTokenSource(Timeout);
        try
        {
            var result = processRunner.RunAsync(command, cancellationToken: cancellation.Token).GetAwaiter().GetResult();
            return result.ExitCode == 0 && output.Length > 0 ? output.ToString().Trim() : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            // A binary for another architecture or a broken download fails at exec; the doctor reports that as a
            // warning instead of losing the whole report.
            return null;
        }
    }
}
