using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.App.Mcp;

namespace SilverModManager.CLI.Tests;

public sealed class McpToolListTests : IAsyncDisposable
{
    private readonly McpTestHost _host = new();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private IReadOnlyList<McpTool> Tools(bool readOnly) => McpToolTable.Create(_host.Context(_host.ClientWithoutMain()), readOnly);

    [Fact]
    public void AFullServer_ListsTheReadOnlyAndTheMutatingTools()
    {
        Tools(readOnly: false).Select(t => t.Name).Should().BeEquivalentTo(
            "app_status", "app_log_errors", "loadouts_list", "loadout_merge_report", "loadout_vanilla_list", "loadout_heal_plan", "doctor", "loadout_apply");
    }

    [Fact]
    public void AReadOnlyServer_LeavesOutTheToolsThatChangeAnything()
    {
        var names = Tools(readOnly: true).Select(t => t.Name).ToList();

        names.Should().NotContain("loadout_apply").And.NotContain("game_launch");
        Tools(readOnly: true).Should().OnlyContain(t => t.ReadOnly);
    }

    [Fact]
    public void EveryMutatingToolIsMarkedAsNotReadOnly_AndNoReadOnlyToolIsMutating()
    {
        var all = Tools(readOnly: false);

        all.Where(t => !t.ReadOnly).Select(t => t.Name).Should().Equal("loadout_apply");
    }

    [Fact]
    public void EveryLoadoutToolRequiresALoadout_AndPointsToLoadoutsList()
    {
        foreach (var tool in Tools(readOnly: false).Where(t => t.Params.Any(p => p.Name == "loadout")))
        {
            tool.Params.Single(p => p.Name == "loadout").Required.Should().BeTrue(tool.Name);
            tool.Description.Should().Contain("loadouts_list", tool.Name);
        }
    }

    [Fact]
    public async Task OnAReadOnlyServer_CallingLoadoutApply_IsAnUnknownToolError()
    {
        var server = new JsonRpcStdioServer(Tools(readOnly: true), NullLogger.Instance, "t", "1");
        using var output = new MemoryStream();

        await server.RunAsync(
            new StringReader("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"loadout_apply","arguments":{"loadout":"x"}}}""" + "\n"),
            output, CancellationToken.None);

        var reply = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(output.ToArray()).Trim())!;
        reply["error"]!["code"]!.GetValue<int>().Should().Be(-32602);
        reply["error"]!["message"]!.GetValue<string>().Should().Contain("loadout_apply");
    }
}
