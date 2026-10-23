using CliWrap;
using CliWrap.Buffered;

namespace SilverModManager.Deployment.Fuse.Mount;

/// <summary>
/// Runs <c>fusermount3</c> from PATH. Not <c>IToolRunner</c>: that one plans Windows tools through Proton for a
/// game installation, and this is a native host binary.
/// </summary>
internal interface IFusermountRunner
{
    Task<(int ExitCode, string StdErr)> Run(IReadOnlyList<string> args, CancellationToken token);
}

internal sealed class CliFusermountRunner : IFusermountRunner
{
    public async Task<(int ExitCode, string StdErr)> Run(IReadOnlyList<string> args, CancellationToken token)
    {
        var result = await Cli.Wrap("fusermount3")
            .WithArguments(args)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(token);
        return (result.ExitCode, result.StandardError);
    }
}
