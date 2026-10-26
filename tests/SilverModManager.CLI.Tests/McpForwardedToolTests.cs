using System.Text.Json.Nodes;
using FluentAssertions;
using SilverModManager.App.Mcp;
using SilverModManager.SingleProcess;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// Tools run verbs in a main process through the real server and client, and the result says what happened in words
/// the model can act on: the verb's own text, never a stack trace.
/// </summary>
public sealed class McpForwardedToolTests : IAsyncDisposable
{
    private const int TimeoutMs = 120_000;

    private readonly McpTestHost _host = new();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private async Task<IReadOnlyList<McpTool>> ToolsAsync(McpLimits? limits = null) =>
        McpToolTable.Create(_host.Context(await _host.StartMainAsync(), limits), readOnly: false);

    private static JsonObject Loadout(string value) => new() { ["loadout"] = value };

    [Fact(Timeout = TimeoutMs)]
    public async Task ASuccessfulVerb_ReturnsItsCapturedText()
    {
        var result = await McpTestHost.CallAsync(await ToolsAsync(), "loadouts_list");

        result.IsError.Should().BeFalse();
        result.Text.Should().Be("Id Name\n1a2b Elden Ring\n");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AJsonReport_RoundTripsByteForByte_AndGetsTheLoadoutAndJsonFlags()
    {
        var result = await McpTestHost.CallAsync(await ToolsAsync(), "loadout_merge_report", Loadout("elden-ring/main"));

        result.IsError.Should().BeFalse();
        result.Text.Should().Be("{\"loadout\":\"elden-ring/main\",\"json\":true}\n");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AHealPlan_AlwaysRunsAsADryRun()
    {
        var result = await McpTestHost.CallAsync(await ToolsAsync(), "loadout_heal_plan", Loadout("abc"));

        result.Text.Should().Be("dry-run=True loadout=abc\n");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task ExitCodeOneFromAReportTool_IsNotAnError_AndSaysTheCode()
    {
        var merge = await McpTestHost.CallAsync(await ToolsAsync(), "loadout_merge_report", Loadout("refused-merge"));
        var heal = await McpTestHost.CallAsync(await ToolsAsync(), "loadout_heal_plan", Loadout("refuse-step"));

        merge.IsError.Should().BeFalse();
        merge.Text.Should().Contain("\"loadout\":\"refused-merge\"").And.Contain("exit code 1");
        heal.IsError.Should().BeFalse();
        heal.Text.Should().Contain("exit code 1");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task ExitCodeOneFromAnotherReadTool_IsAnError_WithTheVerbsText()
    {
        var result = await McpTestHost.CallAsync(await ToolsAsync(), "loadout_vanilla_list", Loadout("failing"));

        result.IsError.Should().BeTrue();
        result.Text.Trim().Should().Be("That loadout is not a FromSoft game.");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task ARefusalFromAMutatingTool_IsAnError_WithTheRefusalAndNothingElse()
    {
        var result = await McpTestHost.CallAsync(await ToolsAsync(), "loadout_apply", Loadout("refuse"));

        result.IsError.Should().BeTrue();
        result.Text.Trim().Should().Be("The game is still running. Close it and try again.");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AnApplyThatPrintsNothing_ReportsThatItApplied()
    {
        var result = await McpTestHost.CallAsync(await ToolsAsync(), "loadout_apply", Loadout("elden-ring/main"));

        result.IsError.Should().BeFalse();
        result.Text.Should().Be("Applied elden-ring/main.");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task GamesList_AsksForJson()
    {
        var result = await McpTestHost.CallAsync(await ToolsAsync(), "games_list");

        result.IsError.Should().BeFalse();
        result.Text.Should().Be("[{\"store\":\"Steam\",\"name\":\"Elden Ring\"}]\n");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task TheHealthCheck_RunsTheChecksAgain_AndAsksForJson()
    {
        var result = await McpTestHost.CallAsync(await ToolsAsync(), "loadout_health_check", Loadout("elden-ring/main"));

        result.IsError.Should().BeFalse();
        result.Text.Should().Be("{\"loadout\":\"elden-ring/main\",\"refresh\":true,\"json\":true}\n");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task GameLaunch_ReturnsTheVerbsText()
    {
        var result = await McpTestHost.CallAsync(await ToolsAsync(), "game_launch", Loadout("elden-ring/main"));

        result.IsError.Should().BeFalse();
        result.Text.Trim().Should().Be("launch in progress for elden-ring/main");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task GameLaunch_WithoutTheWindow_IsAnError_WithTheRefusal()
    {
        var result = await McpTestHost.CallAsync(await ToolsAsync(), "game_launch", Loadout("no-window"));

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("needs the SilverModManager window");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task ACrash_IsAnError_WithAPointerToTheLogAndNoStackTrace()
    {
        var result = await McpTestHost.CallAsync(await ToolsAsync(), "doctor");

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("Details are in the log at").And.NotContain("   at ").And.NotContain("InvalidOperationException");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AVerbThatNeverAnswers_IsAnErrorOnceTheToolsTimeoutPasses()
    {
        var quick = new McpLimits(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300), 10_000);

        var result = await McpTestHost.CallAsync(await ToolsAsync(quick), "loadout_vanilla_list", Loadout("slow"));

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("did not answer within").And.Contain("may still be running");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task LongOutput_IsCut_AndSaysSo()
    {
        var small = McpLimits.Default with { MaxOutputChars = 1_000 };

        var result = await McpTestHost.CallAsync(await ToolsAsync(small), "loadout_vanilla_list", Loadout("big"));

        result.Text.Length.Should().BeLessThan(1_300);
        result.Text.Should().Contain("cut");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task WithNoMainProcess_ASingleSentenceSaysSilverModManagerIsNotRunning()
    {
        var tools = McpToolTable.Create(_host.Context(_host.ClientWithoutMain()), readOnly: false);

        var result = await McpTestHost.CallAsync(tools, "loadouts_list");

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("SilverModManager is not running").And.NotContain("No main process").And.NotContain("   at ");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task WithAHeadlessMain_TheRefusalSaysWhyAndIsAnError()
    {
        _host.ClaimMain(MainKind.HeadlessRun);
        var tools = McpToolTable.Create(_host.Context(_host.ClientWithoutMain()), readOnly: false);

        var result = await McpTestHost.CallAsync(tools, "loadouts_list");

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("A game is running through SilverModManager");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AppStatus_WithAMainRunning_ReportsTheVersionsAndTheHeartbeat()
    {
        var result = await McpTestHost.CallAsync(await ToolsAsync(), "app_status");

        result.IsError.Should().BeFalse();
        result.Text.Should().Contain("SilverModManager 9.9.9 (abc1234)").And.Contain("ProcessId: 4242");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AppStatus_WithAnOlderMainThatHasNoVersionVerb_StillReportsTheHeartbeat()
    {
        await using var olderMain = new McpTestHost(withVersionVerb: false);
        var tools = McpToolTable.Create(olderMain.Context(await olderMain.StartMainAsync()), readOnly: false);

        var result = await McpTestHost.CallAsync(tools, "app_status");

        result.IsError.Should().BeFalse();
        result.Text.Should().Contain("ProcessId: 4242").And.Contain("too old to report its version")
            .And.NotContain("SilverModManager is not running").And.NotContain("Usage:");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AppStatus_WithNoMain_StillAnswers_AndSaysTheAppIsNotRunning()
    {
        var tools = McpToolTable.Create(_host.Context(_host.ClientWithoutMain()), readOnly: false);

        var result = await McpTestHost.CallAsync(tools, "app_status");

        result.IsError.Should().BeFalse();
        result.Text.Should().Contain("SilverModManager is not running");
    }

    [Fact]
    public void TheTestHost_KeepsEverythingUnderTheTempFolder_NeverTheUsersHome()
    {
        var home = Environment.GetEnvironmentVariable("HOME");

        Path.GetFullPath(_host.Directory).Should().StartWith(Path.GetTempPath());
        if (!string.IsNullOrEmpty(home) && !Path.GetTempPath().StartsWith(home)) _host.Directory.Should().NotStartWith(home);
    }
}
