using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Cli;
using SilverModManager.App.Mcp;
using SilverModManager.ProxyConsole;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.Sdk.Settings;
using SilverModManager.SingleProcess;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// A real <see cref="CliServer"/> over a temp sync file, running stand-ins for the verbs the MCP tools call, so a tool
/// call goes through the same socket and renderer path as in the app but touches nothing of the user's.
/// </summary>
public sealed class McpTestHost : IAsyncDisposable
{
    public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("mcp-tests").FullName;

    private CliServer? _server;
    private readonly ServiceProvider _services;

    /// <param name="withVersionVerb">False stands in for a main built before the <c>version</c> verb existed.</param>
    public McpTestHost(bool withVersionVerb = true)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddDefaultRenderers()
            .AddOptionParser<string>(s => (s, null))
            .AddModule("loadout", "Loadout verbs")
            .AddModule("loadouts", "Loadouts verbs")
            .AddModule("games", "Games verbs")
            .AddModule("fromsoft", "FromSoft verbs")
            .AddModule("fromsoft merge", "Merge verbs")
            .AddModule("fromsoft vanilla", "Vanilla verbs")
            .AddVerb(() => Verbs.Synchronize)
            .AddVerb(() => Verbs.ListLoadouts)
            .AddVerb(() => Verbs.MergeList)
            .AddVerb(() => Verbs.VanillaList)
            .AddVerb(() => Verbs.Heal)
            .AddVerb(() => Verbs.Doctor)
            .AddVerb(() => Verbs.ListGames)
            .AddVerb(() => Verbs.Diagnostics)
            .AddVerb(() => Verbs.Launch)
            .AddVerb(() => Verbs.Heartbeat)
            .AddSingleton<CommandLineConfigurator>();
        if (withVersionVerb) services.AddVerb(() => Verbs.Version);
        _services = services.BuildServiceProvider();
    }

    private SyncFile SyncFile() => new(NullLogger<SyncFile>.Instance, FileSystem.Shared.FromUnsanitizedFullPath(Path.Combine(Directory, "test.sync")));

    /// <summary>A client whose sync file has no main process in it.</summary>
    public CliClient ClientWithoutMain() => new(NullLogger<CliClient>.Instance, SyncFile(), _services);

    public async Task<CliClient> StartMainAsync()
    {
        _server = new CliServer(NullLogger<CliServer>.Instance, _services.GetRequiredService<CommandLineConfigurator>(), Substitute.For<ISettingsManager>(), SyncFile(), _services);
        await _server.StartCliServerAsync();
        return ClientWithoutMain();
    }

    public McpToolContext Context(CliClient client, McpLimits? limits = null, string? logPath = null)
    {
        var runtimeDir = System.IO.Directory.CreateDirectory(Path.Combine(Directory, "runtime")).FullName;
        return new(new VerbRunner(client), limits ?? McpLimits.Default, logPath, runtimeDir);
    }

    /// <summary>Records a live main of the given kind in the sync file, as a headless process would.</summary>
    public void ClaimMain(MainKind kind) => SyncFile().TrySetMain(kind, 1).Should().BeTrue();

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.StopAsync(CancellationToken.None);
            _server.Dispose();
        }
        await _services.DisposeAsync();
        System.IO.Directory.Delete(Directory, recursive: true);
    }

    /// <summary>Runs a tool the way the server does and returns its result.</summary>
    public static async Task<ToolResult> CallAsync(IEnumerable<McpTool> tools, string name, JsonObject? arguments = null)
    {
        var tool = tools.Single(t => t.Name == name);
        var element = System.Text.Json.JsonSerializer.SerializeToElement(arguments ?? new JsonObject());
        return await tool.Run(new ToolArgs(element), CancellationToken.None);
    }

    private sealed class Refusal(string message) : UserFacingException(message)
    {
        public override string Title => "Test refusal";
    }

    public static class Verbs
    {
        [Verb("loadout synchronize", "Stand-in for the apply verb")]
        public static Task<int> Synchronize([Injected] IRenderer renderer, [Option("l", "loadout", "Loadout")] string loadout)
        {
            if (loadout == "refuse") throw new Refusal("The game is still running. Close it and try again.");
            return Task.FromResult(0);
        }

        [Verb("loadouts list", "Stand-in for the loadouts list")]
        public static async Task<int> ListLoadouts([Injected] IRenderer renderer)
        {
            await renderer.TextLine("Id Name");
            await renderer.TextLine("1a2b Elden Ring");
            return 0;
        }

        [Verb("fromsoft merge list", "Stand-in for the merge report")]
        public static async Task<int> MergeList([Injected] IRenderer renderer, [Option("l", "loadout", "Loadout")] string loadout, [Option("j", "json", "Json", true)] bool json)
        {
            await renderer.TextLine($"{{\"loadout\":\"{loadout}\",\"json\":{(json ? "true" : "false")}}}");
            return loadout == "refused-merge" ? 1 : 0;
        }

        [Verb("fromsoft vanilla list", "Stand-in for the vanilla list")]
        public static async Task<int> VanillaList([Injected] IRenderer renderer, [Option("l", "loadout", "Loadout")] string loadout, [Injected] CancellationToken token)
        {
            if (loadout == "slow") await Task.Delay(TimeSpan.FromSeconds(30), token);
            if (loadout == "failing")
            {
                await renderer.TextLine("That loadout is not a FromSoft game.");
                return 1;
            }
            if (loadout == "big") await renderer.TextLine(new string('x', 5_000));
            return 0;
        }

        [Verb("fromsoft heal", "Stand-in for the heal plan")]
        public static async Task<int> Heal([Injected] IRenderer renderer, [Option("l", "loadout", "Loadout")] string loadout, [Option("d", "dry-run", "Dry run", true)] bool dryRun)
        {
            await renderer.TextLine($"dry-run={dryRun} loadout={loadout}");
            return loadout == "refuse-step" ? 1 : 0;
        }

        [Verb("doctor", "Stand-in for doctor")]
        public static Task<int> Doctor([Injected] IRenderer renderer, [Option("j", "json", "Json", true)] bool json) =>
            throw new InvalidOperationException("sequence contains no elements");

        [Verb("games list", "Stand-in for the games list")]
        public static async Task<int> ListGames([Injected] IRenderer renderer, [Option("j", "json", "Json", true)] bool json)
        {
            await renderer.TextLine(json ? "[{\"store\":\"Steam\",\"name\":\"Elden Ring\"}]" : "Steam Elden Ring");
            return 0;
        }

        [Verb("loadout diagnostics", "Stand-in for the Health Check")]
        public static async Task<int> Diagnostics([Injected] IRenderer renderer, [Option("l", "loadout", "Loadout")] string loadout,
            [Option("r", "refresh", "Refresh", true)] bool refresh, [Option("j", "json", "Json", true)] bool json)
        {
            await renderer.TextLine($"{{\"loadout\":\"{loadout}\",\"refresh\":{(refresh ? "true" : "false")},\"json\":{(json ? "true" : "false")}}}");
            return 0;
        }

        [Verb("loadout launch", "Stand-in for the launch verb")]
        public static async Task<int> Launch([Injected] IRenderer renderer, [Option("l", "loadout", "Loadout")] string loadout)
        {
            if (loadout == "no-window")
            {
                await renderer.TextLine("Launching needs the SilverModManager window to be open.");
                return 1;
            }
            await renderer.TextLine($"launch in progress for {loadout}");
            return 0;
        }

        [Verb("version", "Stand-in for version")]
        public static async Task<int> Version([Injected] IRenderer renderer)
        {
            await renderer.TextLine("SilverModManager 9.9.9 (abc1234)");
            return 0;
        }

        [Verb("heartbeat", "Stand-in for heartbeat")]
        public static async Task<int> Heartbeat([Injected] IRenderer renderer)
        {
            await renderer.TextLine("ProcessId: 4242 Uptime: 3 minutes");
            return 0;
        }
    }
}
