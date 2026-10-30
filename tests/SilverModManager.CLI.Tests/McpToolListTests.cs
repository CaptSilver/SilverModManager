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
            "app_status", "app_log_errors", "loadouts_list", "loadout_merge_report", "loadout_vanilla_list", "loadout_heal_plan", "doctor", "games_list", "loadout_health_check", "settings_list", "settings_get", "settings_set", "loadout_apply", "game_launch",
            "wabbajack_list", "wabbajack_status", "wabbajack_download", "wabbajack_cancel");
    }

    [Fact]
    public void AReadOnlyServer_LeavesOutTheToolsThatChangeAnything()
    {
        var names = Tools(readOnly: true).Select(t => t.Name).ToList();

        names.Should().NotContain("loadout_apply").And.NotContain("game_launch").And.NotContain("settings_set")
            .And.NotContain("wabbajack_download").And.NotContain("wabbajack_cancel");
        names.Should().Contain(["wabbajack_list", "wabbajack_status"]);
        Tools(readOnly: true).Should().OnlyContain(t => t.ReadOnly);
    }

    [Fact]
    public void EveryMutatingToolIsMarkedAsNotReadOnly_AndNoReadOnlyToolIsMutating()
    {
        var all = Tools(readOnly: false);

        all.Where(t => !t.ReadOnly).Select(t => t.Name).Should().BeEquivalentTo("settings_set", "loadout_apply", "game_launch", "wabbajack_download", "wabbajack_cancel");
    }

    [Fact]
    public void AReadOnlyServer_OffersGamesAndTheHealthCheck()
    {
        Tools(readOnly: true).Select(t => t.Name).Should().Contain(["games_list", "loadout_health_check"]);
    }

    [Fact]
    public void AReadOnlyServer_OffersToReadSettings()
    {
        Tools(readOnly: true).Select(t => t.Name).Should().Contain(["settings_list", "settings_get"]);
    }

    [Fact]
    public void TheSettingsToolsTakeTheKeyAndTheValueAsRequiredArguments()
    {
        var tools = Tools(readOnly: false).ToDictionary(t => t.Name);

        tools["settings_list"].Params.Should().BeEmpty();
        tools["settings_get"].Params.Select(p => (p.Name, p.Required)).Should().Equal(("key", true));
        tools["settings_set"].Params.Select(p => (p.Name, p.Required)).Should().Equal(("key", true), ("value", true));
        tools["settings_get"].Description.Should().Contain("settings_list");
    }

    [Fact]
    public void TheWabbajackToolsTakeTheModlistAndOnlyStatusMayLeaveItOut()
    {
        var tools = Tools(readOnly: false).ToDictionary(t => t.Name);

        tools["wabbajack_download"].Params.Select(p => (p.Name, p.Required)).Should().Equal(("modlist", true));
        tools["wabbajack_status"].Params.Select(p => (p.Name, p.Required)).Should().Equal(("modlist", false));
        tools["wabbajack_list"].Params.Should().BeEmpty();
        tools["wabbajack_cancel"].Params.Should().BeEmpty();
        tools["wabbajack_download"].Description.Should().Contain("wabbajack_list").And.Contain("wabbajack_status");
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
