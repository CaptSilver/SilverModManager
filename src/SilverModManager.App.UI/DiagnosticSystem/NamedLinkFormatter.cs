using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Values;
using SilverModManager.Sdk.Errors;

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
                // The name comes from a mod page. A link that is not a web page is written as plain text, and the
                // characters that would end the link target early are percent-encoded.
                if (value.Uri.Scheme is "https" or "http")
                    writer.Write(ref state, $"[{MarkdownText.Inline(value.Name)}]({value.Uri.AbsoluteUri.Replace("(", "%28").Replace(")", "%29").Replace(" ", "%20")})");
                else
                    writer.Write(ref state, MarkdownText.Inline(value.Name));
                break;
        }
    }
}
