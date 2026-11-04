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
                writer.Write(ref state, value.Uri.ToString().AsSpan());
                break;
            case DiagnosticWriterMode.Markdown:
                // The name comes from a mod page, so the writer escapes it. A link that is not a web page is written as
                // plain text, and the characters that would end the link target early are percent-encoded.
                if (value.Uri.Scheme is "https" or "http")
                {
                    writer.Write(ref state, "[".AsSpan());
                    writer.Write(ref state, value.Name);
                    writer.Write(ref state, $"]({value.Uri.AbsoluteUri.Replace("(", "%28").Replace(")", "%29").Replace(" ", "%20")})".AsSpan());
                }
                else
                {
                    writer.Write(ref state, value.Name);
                }
                break;
        }
    }
}
