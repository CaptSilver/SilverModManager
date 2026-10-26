using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using SilverModManager.App.Mcp;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// Runs the built binary in <c>mcp</c> mode, as Claude Code would, with no display, no session bus and every XDG
/// directory in a temp folder, and checks that stdout carries nothing but protocol frames.
/// </summary>
public sealed class McpProcessTests : IDisposable
{
    private const int TimeoutMs = 120_000;

    private readonly string _root = Directory.CreateTempSubdirectory("mcp-process-tests").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ProcessStartInfo StartInfo(params string[] args)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "SilverModManager.dll"));
        foreach (var arg in args) info.ArgumentList.Add(arg);

        foreach (var name in new[] { "DISPLAY", "WAYLAND_DISPLAY" }) info.Environment.Remove(name);
        foreach (var dir in new[] { "data", "config", "state", "cache", "runtime" }) Directory.CreateDirectory(Path.Combine(_root, dir));
        info.Environment["HOME"] = _root;
        info.Environment["XDG_DATA_HOME"] = Path.Combine(_root, "data");
        info.Environment["XDG_CONFIG_HOME"] = Path.Combine(_root, "config");
        info.Environment["XDG_STATE_HOME"] = Path.Combine(_root, "state");
        info.Environment["XDG_CACHE_HOME"] = Path.Combine(_root, "cache");
        info.Environment["XDG_RUNTIME_DIR"] = Path.Combine(_root, "runtime");
        info.Environment["DBUS_SESSION_BUS_ADDRESS"] = $"unix:path={Path.Combine(_root, "no-bus")}";
        return info;
    }

    private static string Frame(int id, string method, object? @params = null) =>
        System.Text.Json.JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params });

    /// <summary>Sends the lines, closes stdin, and returns what the process wrote to stdout and stderr.</summary>
    private async Task<(List<string> Stdout, string Stderr, int ExitCode)> RunAsync(string[] args, params string[] lines)
    {
        using var process = Process.Start(StartInfo(args))!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        foreach (var line in lines) await process.StandardInput.WriteLineAsync(line);
        process.StandardInput.Close();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        return ((await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList(), await stderr, process.ExitCode);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task StdoutCarriesOnlyProtocolFrames_AndAppStatusSaysTheAppIsNotRunning()
    {
        var (stdout, stderr, exitCode) = await RunAsync([ "mcp" ],
            Frame(1, "initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "test", version = "1" } }),
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            Frame(2, "tools/list"),
            Frame(3, "tools/call", new { name = "app_status", arguments = new { } }),
            Frame(4, "tools/call", new { name = "loadouts_list", arguments = new { } }));

        exitCode.Should().Be(0, stderr);
        var frames = stdout.Select(line => JsonNode.Parse(line)!.AsObject()).ToList();
        frames.Should().OnlyContain(f => f["jsonrpc"]!.GetValue<string>() == "2.0", $"stdout was:\n{string.Join('\n', stdout)}");
        frames.Should().HaveCount(4);

        ById(frames, 1)["result"]!["serverInfo"].Should().NotBeNull();
        ById(frames, 2)["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).Should().Contain(["app_status", "loadout_apply"]);
        var status = ById(frames, 3)["result"]!;
        status["isError"]!.GetValue<bool>().Should().BeFalse();
        status["content"]![0]!["text"]!.GetValue<string>().Should().Contain("SilverModManager is not running");
        var list = ById(frames, 4)["result"]!;
        list["isError"]!.GetValue<bool>().Should().BeTrue();
        list["content"]![0]!["text"]!.GetValue<string>().Should().Contain("SilverModManager is not running");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task ReadOnlyServer_DoesNotListTheMutatingTools()
    {
        var (stdout, stderr, exitCode) = await RunAsync(["mcp", "--read-only"], Frame(1, "tools/list"));

        exitCode.Should().Be(0, stderr);
        var names = JsonNode.Parse(stdout.Single())!["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        names.Should().Contain("app_status").And.NotContain("loadout_apply");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AnUnknownArgument_IsAUsageErrorOnStderr_AndNothingOnStdout()
    {
        var (stdout, stderr, exitCode) = await RunAsync(["mcp", "--bogus"]);

        exitCode.Should().Be(1);
        stdout.Should().BeEmpty();
        stderr.Should().Contain("Usage: silvermodmanager mcp");
    }

    private static JsonObject ById(IEnumerable<JsonObject> frames, int id) => frames.Single(f => f["id"]?.GetValue<int>() == id);
}
