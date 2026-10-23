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
        services.AddSingleton<DoctorCommand>();
        return services.AddVerb(() => Doctor);
    }

    [Verb("doctor", "Reports how each managed game would launch: prefix, Proton, tool runner, Steam launch options and launch lock")]
    private static Task<int> Doctor(
        [Injected] IRenderer renderer,
        [Injected] DoctorCommand command,
        [Option("j", "json", "Print the report as JSON", true)] bool json)
        => command.RunAsync(renderer, json);
}
