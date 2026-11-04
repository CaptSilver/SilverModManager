using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.App.UI.DiagnosticSystem;

internal sealed class DiagnosticWriter : IDiagnosticWriter
{
    private readonly ILogger<DiagnosticWriter> _logger;
    private readonly IValueFormatter[] _formatters;

    public DiagnosticWriter(
        ILogger<DiagnosticWriter> logger,
        IEnumerable<IValueFormatter> formatters)
    {
        _logger = logger;
        _formatters = formatters.ToArray();
    }

    private bool TryGetFormatter<T>([NotNullWhen(true)] out IValueFormatter<T>? formatter) where T : notnull
    {
        formatter = _formatters.OfType<IValueFormatter<T>>().FirstOrDefault();
        if (formatter is not null) return true;

        _logger.LogTrace("Unable to find formatter for type {Type}", typeof(T).ToString());
        return false;
    }

    public void Write<T>(ref DiagnosticWriterState state, T value) where T : notnull
    {
        if (TryGetFormatter<T>(out var formatter))
        {
            formatter.Format(this, ref state, value);
        }
        else
        {
            WriteOutsideText(ref state, value.ToString());
        }
    }

    public void WriteValueType<T>(ref DiagnosticWriterState state, T value) where T : struct
    {
        if (TryGetFormatter<T>(out var formatter))
        {
            formatter.Format(this, ref state, value);
        }
        else
        {
            WriteOutsideText(ref state, value.ToString());
        }
    }

    public void Write(ref DiagnosticWriterState state, ReadOnlySpan<char> value)
    {
        state.StringBuilder.Append(value);
    }

    /// <summary>
    /// Writes text that no formatter claimed: a string, or the <c>ToString</c> of a value. It is outside text (a name
    /// from a mod page, an archive entry), so in Markdown mode it is escaped; the template's own backticks decide how.
    /// </summary>
    private static void WriteOutsideText(ref DiagnosticWriterState state, string? text)
    {
        var safe = state.Mode switch
        {
            DiagnosticWriterMode.Markdown when IsInsideCodeSpan(state.StringBuilder) => MarkdownText.InsideCode(text),
            DiagnosticWriterMode.Markdown when StartsLine(state.StringBuilder) => MarkdownText.Inline(text),
            DiagnosticWriterMode.Markdown => MarkdownText.InlineMidLine(text),
            _ => MarkdownText.PlainText(text),
        };
        state.StringBuilder.Append(safe);
    }

    // Only text at the start of a line can turn into a heading, list or quote, so only there is a block marker defused.
    // A list marker or quote prefix on the line does not count as text: what follows it starts the item.
    private static bool StartsLine(StringBuilder builder)
    {
        for (var i = builder.Length - 1; i >= 0; i--)
        {
            var c = builder[i];
            if (c == '\n') return true;
            if (!(char.IsWhiteSpace(c) || c is '-' or '*' or '+' or '>' or '#' or '.' or ')' || char.IsAsciiDigit(c))) return false;
        }
        return true;
    }

    // The template opens a span or fence with an odd number of backticks. Escaped text never writes one (Inline swaps
    // them out, InsideCode too), so what is in the builder is the template's and the app's Markdown.
    private static bool IsInsideCodeSpan(StringBuilder builder)
    {
        var backticks = 0;
        foreach (var chunk in builder.GetChunks())
        {
            foreach (var c in chunk.Span)
            {
                if (c == '`') backticks++;
            }
        }
        return backticks % 2 == 1;
    }
}
