using System.Text.Encodings.Web;
using System.Text.Json;

namespace SilverModManager.Abstractions.Cli;

/// <summary>
/// The options every <c>--json</c> command prints with.
/// </summary>
public static class CliJson
{
    // Relaxed escaping keeps an apostrophe or an accented letter as it is instead of writing \u0027 or \u00e9. That is safe here
    // because the text goes to a terminal or an MCP client, never into HTML.
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
