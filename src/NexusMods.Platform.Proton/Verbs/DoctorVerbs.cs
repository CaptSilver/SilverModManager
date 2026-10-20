using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Cli;
using NexusMods.Platform.Proton.Doctor;
using NexusMods.Sdk.ProxyConsole;

namespace NexusMods.Platform.Proton.Verbs;

internal static class DoctorVerbs
{
    internal static IServiceCollection AddDoctorVerbs(this IServiceCollection services)
    {
        services.AddSingleton<DoctorReporter>();
        return services.AddVerb(() => Doctor);
    }

    [Verb("doctor", "Reports how each managed game would launch: prefix, Proton, tool runner, Steam launch options and launch lock")]
    private static async Task<int> Doctor(
        [Injected] IRenderer renderer,
        [Injected] IManagedInstallations installations,
        [Injected] DoctorReporter reporter,
        [Option("j", "json", "Print the report as JSON", true)] bool json)
    {
        var report = reporter.Build(installations.List());
        if (json)
        {
            await renderer.TextLine(report.ToJson());
            return 0;
        }

        foreach (var line in DoctorTextFormatter.Format(report)) await renderer.TextLine(line);
        return 0;
    }
}
