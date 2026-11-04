using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Values;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.App.UI.DiagnosticSystem;

/// <summary>
/// Writes Markdown the app built (a list of files, numbered steps) as it is. This is the one string-like value the
/// writer does not escape; anything an emitter builds from names has to escape those names itself. In plain text the
/// markup is stripped instead, so the escapes do not show as backslashes.
/// </summary>
internal sealed class MarkdownFragmentFormatter : IValueFormatter<MarkdownFragment>
{
    // Appended straight to the builder: writer.Write(span) is what counts the template's backticks, and a fragment's
    // backticks (a code span around a name that has one) are not the template's.
    public void Format(IDiagnosticWriter writer, ref DiagnosticWriterState state, MarkdownFragment value) =>
        state.StringBuilder.Append(state.Mode == DiagnosticWriterMode.Markdown ? value.Markdown : MarkdownText.ToPlain(value.Markdown));
}
