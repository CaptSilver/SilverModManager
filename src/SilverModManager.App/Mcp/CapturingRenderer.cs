using System.Text;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.App.Mcp;

/// <summary>
/// Collects what a verb renders into a string, so a caller that has no terminal (the MCP server) can hand it back as
/// text. Text is kept exactly as the verb wrote it, because verbs that print JSON must round-trip byte for byte;
/// tables become Markdown.
/// </summary>
public sealed class CapturingRenderer : IRenderer
{
    private readonly StringBuilder _output = new();
    private readonly object _lock = new();

    /// <summary>Everything rendered so far.</summary>
    public string Output
    {
        get
        {
            lock (_lock) return _output.ToString();
        }
    }

    /// <inheritdoc />
    public ValueTask RenderAsync(IRenderable renderable)
    {
        lock (_lock)
        {
            switch (renderable)
            {
                case Text text:
                    _output.Append(Format(text));
                    break;
                case Table table:
                    AppendTable(table);
                    break;
                case StartProgress or StopProgress or CreateProgressTask or UpdateTask or DeleteProgressTask:
                    // Progress is for a person watching a terminal; there is nothing to capture.
                    break;
                default:
                    throw new NotSupportedException($"Cannot capture a {renderable.GetType().Name} as text.");
            }
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// A verb that clears the screen is redrawing for a terminal; the text it already produced is still part of the
    /// result, so this keeps it.
    /// </summary>
    public ValueTask ClearAsync() => ValueTask.CompletedTask;

    // A template with no arguments is not a format string: JSON braces in it must not be read as placeholders.
    private static string Format(Text text) =>
        text.Arguments.Length == 0 ? text.Template : string.Format(text.Template, text.Arguments);

    private void AppendTable(Table table)
    {
        if (_output.Length > 0 && _output[^1] != '\n') _output.Append('\n');

        AppendRow(table.Columns);
        _output.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", table.Columns.Length))).Append('\n');
        foreach (var row in table.Rows) AppendRow(row);
    }

    private void AppendRow(IRenderable[] cells)
    {
        _output.Append('|');
        foreach (var cell in cells) _output.Append(' ').Append(CellText(cell)).Append(" |");
        _output.Append('\n');
    }

    private static string CellText(IRenderable cell)
    {
        var text = cell switch
        {
            Text t => Format(t),
            _ => throw new NotSupportedException($"Cannot capture a {cell.GetType().Name} in a table cell as text."),
        };
        return text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace("|", "\\|");
    }
}
