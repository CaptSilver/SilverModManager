using System.Text.Json;

namespace SilverModManager.App.Mcp;

/// <summary>One argument a tool accepts, as the client sees it in the tool's input schema.</summary>
/// <param name="Type">The JSON schema type: <c>string</c> or <c>integer</c>.</param>
public sealed record ToolParam(string Name, string Description, bool Required = false, string Type = "string");

/// <summary>What a tool call returns: the text for the model, and whether it is a failure.</summary>
public sealed record ToolResult(string Text, bool IsError);

/// <summary>
/// The arguments of one call, already checked against the tool's <see cref="ToolParam"/>s by the server: required
/// ones are present, and a string never starts with <c>-</c>, so it cannot be read as an option by the verb it ends
/// up in.
/// </summary>
public sealed class ToolArgs(JsonElement arguments)
{
    /// <summary>The string argument, or null when the caller left it out.</summary>
    public string? String(string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>A string argument the tool declares as required, which the server has already checked is there.</summary>
    /// <exception cref="InvalidOperationException">The call reached the tool without the argument.</exception>
    public string RequiredString(string name) =>
        String(name) ?? throw new InvalidOperationException($"The tool was called without its required '{name}' argument.");

    /// <summary>The integer argument, or null when the caller left it out.</summary>
    public int? Int(string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;
}

/// <summary>
/// A tool as the server lists and runs it. A tool that changes anything has <see cref="ReadOnly"/> false; the server
/// runs those one at a time and leaves them out of a read-only server.
/// </summary>
public sealed record McpTool(
    string Name,
    string Description,
    IReadOnlyList<ToolParam> Params,
    bool ReadOnly,
    Func<ToolArgs, CancellationToken, Task<ToolResult>> Run);
