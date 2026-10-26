using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace SilverModManager.App.Mcp;

/// <summary>
/// Serves a table of tools as an MCP server over newline-delimited JSON-RPC 2.0: one JSON message per line in, one per
/// line out. It speaks only the part of MCP that tools need (initialize, ping, tools/list, tools/call); everything
/// else is answered as an unknown method.
/// </summary>
/// <remarks>
/// Read-only tools run concurrently, so replies can come out in a different order than the requests; each carries the
/// id of its request. Tools that change something go through one gate, so two of them never run at once.
/// </remarks>
public sealed class JsonRpcStdioServer
{
    // Oldest first. A client that asks for one of these gets it back; anything else gets the newest.
    private static readonly string[] ProtocolVersions = ["2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25"];

    private const int ParseError = -32700;
    private const int InvalidRequest = -32600;
    private const int MethodNotFound = -32601;
    private const int InvalidParams = -32602;
    private const int InternalError = -32603;

    private readonly Dictionary<string, McpTool> _tools;
    private readonly IReadOnlyList<McpTool> _toolList;
    private readonly ILogger _logger;
    private readonly string _serverName;
    private readonly string _serverVersion;
    private readonly SemaphoreSlim _mutatingGate = new(1, 1);
    private readonly object _writeLock = new();

    public JsonRpcStdioServer(IReadOnlyList<McpTool> tools, ILogger logger, string serverName, string serverVersion)
    {
        _toolList = tools;
        _tools = tools.ToDictionary(t => t.Name);
        _logger = logger;
        _serverName = serverName;
        _serverVersion = serverVersion;
    }

    /// <summary>
    /// Reads requests from <paramref name="input"/> and writes replies to <paramref name="output"/> until the input
    /// ends or <paramref name="token"/> is cancelled. When the input ends, calls already running are finished and
    /// answered first; when the token is cancelled they are abandoned.
    /// </summary>
    public async Task RunAsync(TextReader input, Stream output, CancellationToken token)
    {
        var running = new List<Task>();
        try
        {
            while (true)
            {
                // The read itself may not honour the token (a console stream does not), so stop waiting for it instead.
                var line = await input.ReadLineAsync(token).AsTask().WaitAsync(token);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                var call = Handle(line, output, token);
                if (call is not null)
                {
                    running.RemoveAll(t => t.IsCompleted);
                    running.Add(call);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The host is stopping: nothing is waiting for the calls still running.
        }

        try
        {
            await Task.WhenAll(running);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    /// <summary>Handles one line. Returns the task of a tool call that is still running, or null.</summary>
    private Task? Handle(string line, Stream output, CancellationToken token)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            Send(output, ErrorReply(null, ParseError, "The message is not valid JSON."));
            return null;
        }

        if (node is not JsonObject message)
        {
            Send(output, ErrorReply(null, InvalidRequest, "A request must be a JSON object."));
            return null;
        }

        var hasId = message.ContainsKey("id");
        var id = message["id"]?.DeepClone();
        if (message["method"] is not JsonValue methodValue || !methodValue.TryGetValue<string>(out var method))
        {
            // A reply to something we sent (we send no requests) is ignored; anything else is malformed.
            if (!message.ContainsKey("result") && !message.ContainsKey("error")) Send(output, ErrorReply(id, InvalidRequest, "The message has no method."));
            return null;
        }

        // A notification never gets a reply, whatever the method.
        if (!hasId) return null;

        switch (method)
        {
            case "initialize":
                Send(output, ResultReply(id, Initialize(message["params"] as JsonObject)));
                return null;
            case "ping":
                Send(output, ResultReply(id, new JsonObject()));
                return null;
            case "tools/list":
                Send(output, ResultReply(id, new JsonObject { ["tools"] = new JsonArray(_toolList.Select(Describe).ToArray()) }));
                return null;
            case "tools/call":
                return CallAsync(output, id, message["params"] as JsonObject, token);
            default:
                Send(output, ErrorReply(id, MethodNotFound, $"Method not found: {method}"));
                return null;
        }
    }

    private JsonObject Initialize(JsonObject? parameters)
    {
        var asked = parameters?["protocolVersion"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        return new JsonObject
        {
            ["protocolVersion"] = asked is not null && ProtocolVersions.Contains(asked) ? asked : ProtocolVersions[^1],
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = _serverName, ["version"] = _serverVersion },
        };
    }

    private static JsonNode Describe(McpTool tool)
    {
        var properties = new JsonObject();
        foreach (var p in tool.Params)
        {
            properties[p.Name] = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        var required = tool.Params.Where(p => p.Required).Select(p => (JsonNode)p.Name).ToArray();
        if (required.Length > 0) schema["required"] = new JsonArray(required);

        return new JsonObject
        {
            ["name"] = tool.Name,
            ["description"] = tool.Description,
            ["inputSchema"] = schema,
            ["annotations"] = new JsonObject { ["readOnlyHint"] = tool.ReadOnly, ["destructiveHint"] = !tool.ReadOnly },
        };
    }

    private async Task CallAsync(Stream output, JsonNode? id, JsonObject? parameters, CancellationToken token)
    {
        try
        {
            if (parameters?["name"] is not JsonValue nameValue || !nameValue.TryGetValue<string>(out var name))
            {
                Send(output, ErrorReply(id, InvalidParams, "tools/call needs the name of a tool."));
                return;
            }

            if (!_tools.TryGetValue(name, out var tool))
            {
                Send(output, ErrorReply(id, InvalidParams, $"Unknown tool: {name}"));
                return;
            }

            var arguments = parameters["arguments"];
            if (arguments is not null and not JsonObject)
            {
                Send(output, ErrorReply(id, InvalidParams, "The arguments of a tool call must be an object."));
                return;
            }

            var element = JsonSerializer.SerializeToElement(arguments ?? new JsonObject());
            var invalid = Validate(tool, element);
            Send(output, ResultReply(id, ToolReply(invalid ?? await RunToolAsync(tool, element, token))));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The host is stopping, so there is nobody left to answer.
        }
        catch (Exception e)
        {
            _logger.LogError(e, "MCP request failed");
            Send(output, ErrorReply(id, InternalError, "The server failed while handling the request."));
        }
    }

    private async Task<ToolResult> RunToolAsync(McpTool tool, JsonElement arguments, CancellationToken token)
    {
        var gated = !tool.ReadOnly;
        if (gated) await _mutatingGate.WaitAsync(token);
        try
        {
            return await tool.Run(new ToolArgs(arguments), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // The model gets a sentence; the exception and its trace go to the log only.
            _logger.LogError(e, "MCP tool {Tool} failed", tool.Name);
            return new ToolResult($"The {tool.Name} tool failed unexpectedly. The details are in the MCP server's log.", true);
        }
        finally
        {
            if (gated) _mutatingGate.Release();
        }
    }

    /// <summary>Returns the error to answer with when the arguments do not fit the tool, or null.</summary>
    private static ToolResult? Validate(McpTool tool, JsonElement arguments)
    {
        foreach (var p in tool.Params)
        {
            if (!arguments.TryGetProperty(p.Name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                if (p.Required) return new ToolResult($"The argument '{p.Name}' is required. {p.Description}", true);
                continue;
            }

            switch (p.Type)
            {
                case "string" when value.ValueKind != JsonValueKind.String:
                    return new ToolResult($"The argument '{p.Name}' must be a string.", true);
                // Values end up as command-line arguments, where a leading dash would be read as an option.
                case "string" when value.GetString() is { } text && text.StartsWith('-'):
                    return new ToolResult($"The argument '{p.Name}' must not start with '-'.", true);
                case "integer" when value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out _):
                    return new ToolResult($"The argument '{p.Name}' must be a whole number.", true);
            }
        }
        return null;
    }

    private static JsonObject ToolReply(ToolResult result) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result.Text }),
        ["isError"] = result.IsError,
    };

    private static JsonObject ResultReply(JsonNode? id, JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject ErrorReply(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    // One frame per line: the serialized JSON never contains a raw newline, and the lock keeps concurrent replies apart.
    private void Send(Stream output, JsonObject frame)
    {
        var bytes = Encoding.UTF8.GetBytes(frame.ToJsonString() + "\n");
        lock (_writeLock)
        {
            output.Write(bytes);
            output.Flush();
        }
    }
}
