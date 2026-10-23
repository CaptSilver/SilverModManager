using System.Text.RegularExpressions;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Markdown.Avalonia;
using Markdown.Avalonia.Parsers;
using Markdown.Avalonia.Plugins;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.App.UI.Controls.MarkdownRenderer;

/// <summary>
/// Replaces the library's fenced code block with one that has a copy button beside the code.
/// The block keeps the library's <c>CodeBlock</c> class, so the renderer's code block styles still apply.
/// Indented code blocks are left to the library.
/// </summary>
internal sealed class CodeBlockCopyPlugin(ICommand copyCommand) : IMdAvPlugin
{
    // Name the library registers its fenced code block parser under.
    private const string FencedParserName = "CodeBlocksWithLangEvaluator";

    public void Setup(SetupInfo info) => info.Register(new FencedBlockOverride(copyCommand));

    /// <summary>
    /// The text between the fences of the block whose opening fence is <paramref name="opening"/>,
    /// without the line break that precedes the closing fence, and where the block ends in <paramref name="text"/>.
    /// An unclosed block runs to the end of the document.
    /// </summary>
    internal static (string Code, int End) ReadBlock(string text, Match opening)
    {
        var fence = opening.Groups[1].Value;
        var bodyStart = opening.Index + opening.Length;

        var closing = new Regex($"^[ ]{{0,3}}{Regex.Escape(fence)}`*[ ]*(\\n|\\z)", RegexOptions.Multiline).Match(text, bodyStart);
        if (!closing.Success) return (text[bodyStart..].TrimEnd('\n'), text.Length);

        var body = text[bodyStart..closing.Index];
        if (body.EndsWith('\n')) body = body[..^1];
        return (body, closing.Index + closing.Length);
    }

    private sealed class FencedBlockOverride(ICommand copyCommand) : IBlockOverride
    {
        public string ParserName => FencedParserName;

        public IEnumerable<Control> Convert(string text, Match match, ParseStatus status, IMarkdownEngine engine, out int parseTextBegin, out int parseTextEnd)
        {
            var (code, end) = ReadBlock(text, match);
            parseTextBegin = match.Index;
            parseTextEnd = end;
            return [CreateBlock(code, copyCommand)];
        }
    }

    private static Control CreateBlock(string code, ICommand copyCommand)
    {
        var button = new StandardButton
        {
            Text = "Copy",
            LeftIcon = IconValues.Copy,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Command = copyCommand,
            CommandParameter = code,
        };
        button.SetValue(StandardButton.ShowIconProperty, StandardButton.ShowIconOptions.Left);

        // Same shape as the library's block: code doesn't wrap, so a long command stays one line and
        // scrolls sideways. Every part carries the CodeBlock class, which is what the code font style
        // keys on, so that style leaves the button's own label alone.
        var body = new SelectableTextBlock { Text = code, TextWrapping = TextWrapping.NoWrap };
        var scroller = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        scroller.Classes.Add(Markdown.Avalonia.Markdown.CodeBlockClass);
        body.Classes.Add(Markdown.Avalonia.Markdown.CodeBlockClass);

        // The button gets its own column so it never covers the first line of code.
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { scroller, button } };
        Grid.SetColumn(button, 1);

        var block = new Border { Child = grid };
        block.Classes.Add(Markdown.Avalonia.Markdown.CodeBlockClass);
        return block;
    }
}
