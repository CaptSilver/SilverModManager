using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Cli;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Selftest;
using NexusMods.Platform.Proton.Steam;
using NexusMods.Sdk;
using NexusMods.Sdk.ProxyConsole;

namespace NexusMods.Platform.Proton.Verbs;

internal static class SelftestVerbs
{
    internal static IServiceCollection AddSelftestVerbs(this IServiceCollection services)
        => services.AddVerb(() => Selftest);

    [Verb("selftest", "Checks the installed binary and this machine offline: the launcher shim, file locks, SIGTERM handling, notify-send, FUSE and the Windows program runners")]
    private static async Task<int> Selftest(
        [Injected] IRenderer renderer,
        [Injected] IOSInterop interop,
        [Injected] IEnvironmentReader environment,
        [Injected] IFileSystem fileSystem,
        [Injected] IProcessRunner processRunner,
        [Injected] CancellationToken cancellationToken,
        [Option("j", "json", "Print the report as JSON", true)] bool json)
    {
        var shim = new LauncherShim(LauncherShim.ResolveDataHome(environment, fileSystem));
        var runner = new SelftestRunner(interop.GetRunningExecutablePath(out _), shim, environment, processRunner);
        var report = await runner.RunAsync(cancellationToken);

        if (json) await renderer.TextLine(report.ToJson());
        else foreach (var line in report.ToLines()) await renderer.TextLine(line);
        return report.ExitCode;
    }
}
