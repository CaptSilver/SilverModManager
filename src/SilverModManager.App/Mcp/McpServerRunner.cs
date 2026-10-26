using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.CrossPlatform;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Settings;
using SilverModManager.SingleProcess;

namespace SilverModManager.App.Mcp;

/// <summary>Runs the MCP server on the process's stdin and stdout.</summary>
public static class McpServerRunner
{
    /// <summary>
    /// Serves until the client closes stdin or the host stops (Claude Code ends a server with SIGTERM), and returns the
    /// process exit code.
    /// </summary>
    public static async Task<int> RunAsync(IServiceProvider services, StartupMode startupMode)
    {
        // Take the real stdout for the protocol, then point Console.Out at stderr: any stray write, now or in code added
        // later, lands in the client's log instead of corrupting a frame.
        using var protocolOut = Console.OpenStandardOutput();
        Console.SetOut(Console.Error);
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));

        var fileSystem = services.GetRequiredService<IFileSystem>();
        var logging = services.GetRequiredService<ISettingsManager>().Get<LoggingSettings>();
        var context = new McpToolContext(
            new VerbRunner(services.GetRequiredService<CliClient>()),
            McpLimits.Default,
            logging.MainProcessLogFilePath.ToPath(fileSystem).GetFullPath(),
            Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"));

        var server = new JsonRpcStdioServer(
            McpToolTable.Create(context, startupMode.McpReadOnly),
            services.GetRequiredService<ILogger<JsonRpcStdioServer>>(),
            "silvermodmanager",
            ApplicationConstants.Version.ToString());

        await server.RunAsync(input, protocolOut, services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
        return 0;
    }
}
