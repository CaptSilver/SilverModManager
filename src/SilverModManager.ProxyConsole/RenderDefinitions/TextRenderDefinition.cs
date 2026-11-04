using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.ProxyConsole;
using Rendering = Spectre.Console.Rendering;
using Console = Spectre.Console;

namespace SilverModManager.ProxyConsole.RenderDefinitions;

/// <summary>
/// A definition for rendering <see cref="Text"/>s to the console using Spectre.Console.
/// </summary>
public class TextRenderDefinition() : ARenderableDefinition<Text>("718E119C-FABF-4A63-9B52-674747A9CFD0")
{
    /// <inheritdoc />
    protected override async ValueTask<Rendering.IRenderable> ToSpectreAsync(Text renderable, Func<IRenderable,
        ValueTask<Rendering.IRenderable>> subConvert)
    {
        return new UnwrappedText(Format(renderable));
    }

    /// <summary>
    /// The text with its arguments applied, or the template verbatim when there are none, with terminal control
    /// characters dropped. Each argument is flattened to one line first, so a value cannot add output lines; only the
    /// template's own line feeds and tabs stay.
    /// </summary>
    internal static string Format(Text renderable)
    {
        var text = renderable.Arguments.Length == 0
            ? renderable.Template
            : string.Format(renderable.Template, renderable.Arguments.Select(a => (object)MarkdownText.PlainText(a)).ToArray());
        return MarkdownText.PlainText(text, keepLayout: true);
    }
}
