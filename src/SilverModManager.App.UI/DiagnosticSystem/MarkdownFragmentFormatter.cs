using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Values;

namespace SilverModManager.App.UI.DiagnosticSystem;

/// <summary>
/// Writes Markdown the app built (a list of files, numbered steps) as it is. This is the one string-like value the
/// writer does not escape; anything an emitter builds from names has to escape those names itself.
/// </summary>
internal sealed class MarkdownFragmentFormatter : IValueFormatter<MarkdownFragment>
{
    public void Format(IDiagnosticWriter writer, ref DiagnosticWriterState state, MarkdownFragment value) =>
        writer.Write(ref state, value.Markdown.AsSpan());
}
