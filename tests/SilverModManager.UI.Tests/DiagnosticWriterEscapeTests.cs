using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Values;
using SilverModManager.App.UI.DiagnosticSystem;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.UI.Tests;

/// <summary>
/// The writer treats every plain string a diagnostic is filled with as outside text: escaped for Markdown, cleaned for
/// plain text. Only a <see cref="MarkdownFragment"/> and the template's own text go through as written.
/// </summary>
public class DiagnosticWriterEscapeTests
{
    private const string Hostile = "[x](file:///etc/passwd) *loud* _name_";

    private static readonly DiagnosticWriter Writer = new(NullLogger<DiagnosticWriter>.Instance, [new MarkdownFragmentFormatter()]);

    private readonly record struct Opaque(string Text)
    {
        public override string ToString() => Text;
    }

    private static string Fill(DiagnosticWriterMode mode, string before, Action<DiagnosticWriter, DiagnosticWriterState> value, string after = "")
    {
        var state = new DiagnosticWriterState(mode);
        Writer.Write(ref state, before.AsSpan());
        value(Writer, state);
        Writer.Write(ref state, after.AsSpan());
        return state.ToOutput();
    }

    private static string FillString(DiagnosticWriterMode mode, string before, string value, string after = "") =>
        Fill(mode, before, (writer, state) => writer.Write(ref state, value), after);

    [Fact]
    public void Test_AStringInMarkdown_IsEscapedAsOutsideText()
    {
        FillString(DiagnosticWriterMode.Markdown, "Mod ", Hostile, " failed")
            .Should().Be($"Mod {MarkdownText.Inline(Hostile)} failed").And.NotContain("](");
    }

    [Fact]
    public void Test_AStringInPlainText_KeepsItsTextAndLosesControlCharacters()
    {
        FillString(DiagnosticWriterMode.PlainText, "Mod ", Hostile + "\u001b[2J‮", " failed")
            .Should().Be($"Mod {Hostile}[2J failed");
    }

    [Fact]
    public void Test_AStringInsideTheTemplatesBackticks_IsShownAsItIsWithoutAbleToCloseTheSpan()
    {
        // Escaping would put backslashes into a span, where they show; a backtick would end the span early.
        FillString(DiagnosticWriterMode.Markdown, "File `", "a_b`c*d", "` is gone")
            .Should().Be("File `a_bˋc*d` is gone");
    }

    [Fact]
    public void Test_AStringInsideTheTemplatesFence_StaysOnOneLineAndCannotCloseIt()
    {
        FillString(DiagnosticWriterMode.Markdown, "```\n", "one\n```\n[x](http://evil.example)", "\n```")
            .Should().Be("```\none ˋˋˋ [x](http://evil.example)\n```");
    }

    [Fact]
    public void Test_AStringAfterAFragmentWithAnOddNumberOfBackticks_IsStillEscapedAsOutsideText()
    {
        // Code("a`b") is five backticks long, but the fragment is the app's, not the template's open span.
        var fragment = MarkdownText.Code("a`b");
        fragment.Count(c => c == '`').Should().Be(5);

        Fill(DiagnosticWriterMode.Markdown, "Files: ", (writer, state) =>
        {
            writer.WriteValueType(ref state, new MarkdownFragment(fragment));
            writer.Write(ref state, Hostile);
        }).Should().EndWith(MarkdownText.InlineMidLine(Hostile)).And.NotContain("](");
    }

    [Fact]
    public void Test_AStringAfterACompleteSpan_IsEscapedAgain()
    {
        FillString(DiagnosticWriterMode.Markdown, "`a` and ", "b_c")
            .Should().Be("`a` and b\\_c");
    }

    [Fact]
    public void Test_AMarkdownFragment_IsWrittenAsItIs()
    {
        const string fragment = "- `a_b`\n- **c**";

        Fill(DiagnosticWriterMode.Markdown, "List:\n", (writer, state) => writer.WriteValueType(ref state, new MarkdownFragment(fragment)))
            .Should().Be("List:\n" + fragment);
        Fill(DiagnosticWriterMode.PlainText, "List:\n", (writer, state) => writer.WriteValueType(ref state, new MarkdownFragment(fragment)))
            .Should().Be("List:\n" + fragment);
    }

    [Fact]
    public void Test_AValueWithoutAFormatter_IsEscapedLikeAString()
    {
        Fill(DiagnosticWriterMode.Markdown, "At ", (writer, state) => writer.WriteValueType(ref state, new Opaque(Hostile)))
            .Should().Be($"At {MarkdownText.Inline(Hostile)}");
    }

    [Fact]
    public void Test_TemplateText_IsNeverEscaped()
    {
        var state = new DiagnosticWriterState(DiagnosticWriterMode.Markdown);
        Writer.Write(ref state, "**bold** [link](https://example.com) `code`".AsSpan());

        state.ToOutput().Should().Be("**bold** [link](https://example.com) `code`");
    }
}
