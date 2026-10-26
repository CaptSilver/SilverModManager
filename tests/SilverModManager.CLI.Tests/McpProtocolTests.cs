using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.App.Mcp;

namespace SilverModManager.CLI.Tests;

/// <summary>The JSON-RPC framing and method handling of the stdio server, driven with plain JSON lines.</summary>
public sealed class McpProtocolTests
{
    private static readonly McpTool Echo = new(
        "echo", "Echoes its text argument", [new ToolParam("text", "What to echo", Required: true)], ReadOnly: true,
        (args, _) => Task.FromResult(new ToolResult(args.String("text")!, false)));

    private static readonly McpTool Boom = new(
        "boom", "Throws", [], ReadOnly: true,
        (_, _) => throw new InvalidOperationException("secret internal detail"));

    private static readonly McpTool Change = new(
        "change", "Changes something", [], ReadOnly: false,
        (_, _) => Task.FromResult(new ToolResult("changed", false)));

    private static async Task<List<JsonObject>> ServeAsync(IReadOnlyList<McpTool> tools, params string[] lines)
    {
        var server = new JsonRpcStdioServer(tools, NullLogger.Instance, "silvermodmanager-test", "1.2.3");
        using var output = new MemoryStream();
        await server.RunAsync(new StringReader(string.Join("\n", lines) + "\n"), output, CancellationToken.None);
        return Encoding.UTF8.GetString(output.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!.AsObject())
            .ToList();
    }

    private static string Request(int id, string method, object? @params = null) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params });

    [Fact]
    public async Task Initialize_EchoesAKnownProtocolVersion_AndAdvertisesTools()
    {
        var replies = await ServeAsync([Echo], Request(1, "initialize", new { protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "t", version = "1" } }));

        var result = replies.Single()["result"]!.AsObject();
        result["protocolVersion"]!.GetValue<string>().Should().Be("2025-03-26");
        result["capabilities"]!["tools"].Should().NotBeNull();
        result["serverInfo"]!["name"]!.GetValue<string>().Should().Be("silvermodmanager-test");
        result["serverInfo"]!["version"]!.GetValue<string>().Should().Be("1.2.3");
    }

    [Fact]
    public async Task Initialize_AnswersWithItsNewestVersion_WhenTheClientAsksForOneItDoesNotKnow()
    {
        var replies = await ServeAsync([Echo], Request(1, "initialize", new { protocolVersion = "1999-01-01" }));

        var version = replies.Single()["result"]!["protocolVersion"]!.GetValue<string>();
        version.Should().NotBe("1999-01-01").And.MatchRegex(@"^\d{4}-\d{2}-\d{2}$");
    }

    [Fact]
    public async Task AnUnknownMethod_IsMethodNotFound()
    {
        var replies = await ServeAsync([Echo], Request(7, "resources/list"));

        replies.Single()["id"]!.GetValue<int>().Should().Be(7);
        replies.Single()["error"]!["code"]!.GetValue<int>().Should().Be(-32601);
    }

    [Fact]
    public async Task ANotification_GetsNoReply_EvenWhenTheMethodIsUnknown()
    {
        var replies = await ServeAsync([Echo],
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":3}}""",
            """{"jsonrpc":"2.0","method":"something/unknown"}""",
            Request(2, "ping"));

        replies.Should().ContainSingle().Which["id"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public async Task Ping_ReplyIsAnEmptyObject()
    {
        var replies = await ServeAsync([Echo], Request(1, "ping"));

        replies.Single()["result"]!.AsObject().Should().BeEmpty();
    }

    [Fact]
    public async Task AMalformedLine_IsAParseError_AndTheServerKeepsRunning()
    {
        var replies = await ServeAsync([Echo], "{this is not json", Request(5, "ping"));

        replies.Should().HaveCount(2);
        replies[0]["error"]!["code"]!.GetValue<int>().Should().Be(-32700);
        replies[0]["id"].Should().BeNull();
        replies[1]["id"]!.GetValue<int>().Should().Be(5);
    }

    [Fact]
    public async Task ALineThatIsNotARequest_IsAnInvalidRequest_AndTheServerKeepsRunning()
    {
        var replies = await ServeAsync([Echo], "[1,2,3]", "42", Request(5, "ping"));

        replies.Select(r => r["error"]?["code"]?.GetValue<int>()).Should().Equal(-32600, -32600, null);
    }

    [Fact]
    public async Task AReplyFromTheClient_IsIgnored()
    {
        var replies = await ServeAsync([Echo], """{"jsonrpc":"2.0","id":9,"result":{}}""", Request(2, "ping"));

        replies.Should().ContainSingle().Which["id"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public async Task BlankLines_AreSkipped()
    {
        var replies = await ServeAsync([Echo], "", "   ", Request(2, "ping"));

        replies.Should().ContainSingle();
    }

    [Fact]
    public async Task ToolsList_DescribesEachToolWithItsSchemaAndAnnotations()
    {
        var replies = await ServeAsync([Echo, Change], Request(1, "tools/list"));

        var tools = replies.Single()["result"]!["tools"]!.AsArray();
        tools.Select(t => t!["name"]!.GetValue<string>()).Should().Equal("echo", "change");
        var echo = tools[0]!;
        echo["description"]!.GetValue<string>().Should().Be("Echoes its text argument");
        echo["inputSchema"]!["type"]!.GetValue<string>().Should().Be("object");
        echo["inputSchema"]!["properties"]!["text"]!["type"]!.GetValue<string>().Should().Be("string");
        echo["inputSchema"]!["required"]!.AsArray().Select(r => r!.GetValue<string>()).Should().Equal("text");
        echo["annotations"]!["readOnlyHint"]!.GetValue<bool>().Should().BeTrue();
        tools[1]!["annotations"]!["readOnlyHint"]!.GetValue<bool>().Should().BeFalse();
        tools[1]!["annotations"]!["destructiveHint"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task ToolsCall_ReturnsTheToolsTextAsContent()
    {
        var replies = await ServeAsync([Echo], Request(1, "tools/call", new { name = "echo", arguments = new { text = "hello" } }));

        var result = replies.Single()["result"]!;
        result["isError"]!.GetValue<bool>().Should().BeFalse();
        result["content"]![0]!["type"]!.GetValue<string>().Should().Be("text");
        result["content"]![0]!["text"]!.GetValue<string>().Should().Be("hello");
    }

    [Fact]
    public async Task ToolsCall_OfAnUnknownTool_IsInvalidParams()
    {
        var replies = await ServeAsync([Echo], Request(1, "tools/call", new { name = "loadout_apply", arguments = new { } }));

        replies.Single()["error"]!["code"]!.GetValue<int>().Should().Be(-32602);
    }

    [Fact]
    public async Task ToolsCall_WithoutAName_IsInvalidParams()
    {
        var replies = await ServeAsync([Echo], Request(1, "tools/call", new { arguments = new { } }));

        replies.Single()["error"]!["code"]!.GetValue<int>().Should().Be(-32602);
    }

    [Fact]
    public async Task AMissingRequiredArgument_IsAToolErrorThatNamesIt()
    {
        var replies = await ServeAsync([Echo], Request(1, "tools/call", new { name = "echo", arguments = new { } }));

        var result = replies.Single()["result"]!;
        result["isError"]!.GetValue<bool>().Should().BeTrue();
        result["content"]![0]!["text"]!.GetValue<string>().Should().Contain("text");
    }

    [Fact]
    public async Task AStringArgumentThatStartsWithADash_IsRefused_SoItCannotBecomeAnOption()
    {
        var replies = await ServeAsync([Echo], Request(1, "tools/call", new { name = "echo", arguments = new { text = "--dry-run" } }));

        var result = replies.Single()["result"]!;
        result["isError"]!.GetValue<bool>().Should().BeTrue();
        result["content"]![0]!["text"]!.GetValue<string>().Should().Contain("text").And.Contain("-");
    }

    [Fact]
    public async Task AToolThatThrows_IsAToolErrorWithoutTheExceptionDetails_AndTheServerKeepsRunning()
    {
        var replies = await ServeAsync([Boom, Echo],
            Request(1, "tools/call", new { name = "boom", arguments = new { } }),
            Request(2, "ping"));

        var boom = replies.Single(r => r["id"]!.GetValue<int>() == 1)["result"]!;
        boom["isError"]!.GetValue<bool>().Should().BeTrue();
        boom["content"]![0]!["text"]!.GetValue<string>().Should().NotContain("secret internal detail").And.NotContain("InvalidOperation");
        replies.Should().Contain(r => r["id"]!.GetValue<int>() == 2);
    }

    [Fact]
    public void ARequiredArgumentThatIsMissing_ThrowsNamingIt()
    {
        var args = new ToolArgs(JsonSerializer.SerializeToElement(new { other = "x" }));

        args.Invoking(a => a.RequiredString("loadout")).Should().Throw<InvalidOperationException>().WithMessage("*'loadout'*");
    }

    [Fact]
    public async Task EachReplyIsOneLine_WhateverTheToolPrints()
    {
        var multiline = new McpTool("multi", "Prints lines", [], true, (_, _) => Task.FromResult(new ToolResult("a\nb\r\nc \"quoted\"", false)));
        var server = new JsonRpcStdioServer([multiline], NullLogger.Instance, "t", "1");
        using var output = new MemoryStream();

        await server.RunAsync(new StringReader(Request(1, "tools/call", new { name = "multi", arguments = new { } }) + "\n"), output, CancellationToken.None);

        var lines = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().ContainSingle();
        JsonNode.Parse(lines[0])!["result"]!["content"]![0]!["text"]!.GetValue<string>().Should().Be("a\nb\r\nc \"quoted\"");
    }

    [Fact]
    public async Task MutatingTools_RunOneAtATime_WhileReadOnlyToolsOverlap()
    {
        var running = 0;
        var most = 0;
        var readers = 0;
        var mostReaders = 0;
        static void Bump(ref int current, ref int max, int by)
        {
            var now = Interlocked.Add(ref current, by);
            int seen;
            while ((seen = max) < now && Interlocked.CompareExchange(ref max, now, seen) != seen) { }
        }
        var slowChange = new McpTool("slow_change", "Changes slowly", [], false, async (_, _) =>
        {
            Bump(ref running, ref most, 1);
            await Task.Delay(100);
            Interlocked.Decrement(ref running);
            return new ToolResult("ok", false);
        });
        var slowRead = new McpTool("slow_read", "Reads slowly", [], true, async (_, _) =>
        {
            Bump(ref readers, ref mostReaders, 1);
            await Task.Delay(300);
            Interlocked.Decrement(ref readers);
            return new ToolResult("ok", false);
        });

        var replies = await ServeAsync([slowChange, slowRead],
            Request(1, "tools/call", new { name = "slow_change", arguments = new { } }),
            Request(2, "tools/call", new { name = "slow_change", arguments = new { } }),
            Request(3, "tools/call", new { name = "slow_read", arguments = new { } }),
            Request(4, "tools/call", new { name = "slow_read", arguments = new { } }));

        replies.Should().HaveCount(4);
        most.Should().Be(1);
        mostReaders.Should().Be(2);
    }
}
