using System.Diagnostics;
using Humanizer;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Sdk;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.App.Commandline;


/// <summary>
/// These verbs are used for checking the status of SilverModManager.
/// </summary>
public static class StatusVerbs
{
    // ReSharper disable once UnusedMethodReturnValue.Global
    public static IServiceCollection AddStatusVerbs(this IServiceCollection services) =>
        services.AddVerb(() => Heartbeat)
            .AddVerb(() => Version);

    public const string HeartbeatCommand = "heartbeat";
    /// <summary>
    /// Returns the processId and the uptime of the SilverModManager process.
    /// </summary>
    /// <param name="renderer"></param>
    /// <returns></returns>
    [Verb(HeartbeatCommand, "Returns process uptime for SilverModManager.")]
    private static async Task<int> Heartbeat([Injected] IRenderer renderer)
    {
        var process = Process.GetCurrentProcess();
        
        var startTime = process.StartTime;
        var uptime = DateTime.Now - startTime;
        
        await renderer.TextLine($"ProcessId: {process.Id} Uptime: {uptime.Humanize()}");

        return 0;
    }

    /// <summary>
    /// Prints the version of the build and the commit it came from, so a caller can tell which build is running
    /// without reading logs.
    /// </summary>
    [Verb("version", "Prints the SilverModManager version and the commit it was built from.")]
    private static async Task<int> Version([Injected] IRenderer renderer)
    {
        var commit = ApplicationConstants.CommitHash is { } hash ? $" ({hash})" : "";
        await renderer.TextLine($"{ApplicationConstants.ProductName} {ApplicationConstants.Version}{commit}");
        return 0;
    }
}
