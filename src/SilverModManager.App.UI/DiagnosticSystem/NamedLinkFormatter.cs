using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Values;

namespace SilverModManager.App.UI.DiagnosticSystem;

internal sealed class NamedLinkFormatter : IValueFormatter<NamedLink>
{
    public void Format(IDiagnosticWriter writer, ref DiagnosticWriterState state, NamedLink value)
    {
        switch (state.Mode)
        {
            case DiagnosticWriterMode.PlainText:
                writer.Write(ref state, value.Uri.ToString());
                break;
            case DiagnosticWriterMode.Markdown:
                writer.Write(ref state, $"[{value.Name}]({value.Uri.ToString()})");
                break;
        }
    }
}
