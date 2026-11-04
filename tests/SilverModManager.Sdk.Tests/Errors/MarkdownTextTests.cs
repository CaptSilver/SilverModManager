using FluentAssertions;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Sdk.Tests.Errors;

public class MarkdownTextTests
{
    private const string Z = "\u200B";

    [Theory]
    [InlineData("Plain name 1.2", "Plain name 1.2")]
    [InlineData("1. first", Z + "1. first")]
    [InlineData("2) first", Z + "2) first")]
    [InlineData("# heading", Z + "# heading")]
    [InlineData("- item", Z + "- item")]
    [InlineData("> quote", Z + "> quote")]
    [InlineData("```fence", "\u02CB\u02CB\u02CBfence")]
    [InlineData("a`b", "a\u02CBb")]
    [InlineData("a - b # c > d", "a - b # c > d")]
    [InlineData("**bold**", "\\*\\*bold\\*\\*")]
    [InlineData("a_b_c", "a\\_b\\_c")]
    [InlineData("~~gone~~", "\\~\\~gone\\~\\~")]
    [InlineData("[x](file:///etc/passwd)", "[x]" + Z + "(file:///etc/passwd)")]
    [InlineData("![img](https://evil.example/a.png)", "![img]" + Z + "(https://evil.example/a.png)")]
    [InlineData("[a][b]", "[a]" + Z + "[b]" + Z)]
    [InlineData("C:\\mods\\x", "C:\\\\mods\\\\x")]
    public void Test_Inline_DefusesLinksImagesEmphasisAndBlockMarkers(string input, string expected)
    {
        MarkdownText.Inline(input).Should().Be(expected);
    }

    [Fact]
    public void Test_Inline_LeavesNoBacktickToPairWithTheNextCodeSpan()
    {
        // The renderer matches code spans over the whole paragraph, so a lone backtick in escaped text pairs with the
        // fence of a later Code(...) and leaves that span's content outside, where it is read as Markdown.
        MarkdownText.Inline("'/g/a`b'").Should().NotContain("`");

        var paragraph = $"{MarkdownText.Inline("'/g/a`b'")}\n{MarkdownText.Code("[x](https://evil.example)")}";
        var spans = System.Text.RegularExpressions.Regex.Matches(paragraph, @"(?<!\\)(`+)(.+?)(?<!`)\1(?!`)", System.Text.RegularExpressions.RegexOptions.Singleline);

        spans.Should().ContainSingle().Which.Groups[2].Value.Should().Be("[x](https://evil.example)");
    }

    [Fact]
    public void Test_Inline_NeverLeavesALinkTargetRightAfterABracket()
    {
        foreach (var payload in new[] { "[x](file:///etc/passwd)", "a](b)", "![x](https://e.example/i.png)", "[x]   (http://a.example)" })
        {
            MarkdownText.Inline(payload).Should().NotContain("](");
        }
    }

    [Fact]
    public void Test_Inline_TurnsLineBreaksIntoSpacesAndDropsOtherControlCharacters()
    {
        MarkdownText.Inline("a\r\nb\nc\td\u0000e\u001bf").Should().Be("a  b c def");
    }

    [Fact]
    public void Test_Inline_DropsBidiOverrides()
    {
        MarkdownText.Inline("a\u202Eb\u200Bc").Should().Be("abc");
    }

    [Fact]
    public void Test_Inline_NullAndEmptyGiveEmpty()
    {
        MarkdownText.Inline(null).Should().BeEmpty();
        MarkdownText.Inline("").Should().BeEmpty();
    }

    [Theory]
    [InlineData("Plain", "`Plain`")]
    [InlineData("a`b", "``a`b``")]
    [InlineData("a``b", "```a``b```")]
    [InlineData("`lead", "`` `lead ``")]
    [InlineData("trail`", "`` trail` ``")]
    [InlineData("[x](file:///etc/passwd)", "`[x](file:///etc/passwd)`")]
    [InlineData("line\nbreak", "`line break`")]
    public void Test_Code_FencesLongerThanAnyBacktickRun(string input, string expected)
    {
        MarkdownText.Code(input).Should().Be(expected);
    }

    [Fact]
    public void Test_Code_EmptyGivesEmpty()
    {
        MarkdownText.Code("").Should().BeEmpty();
        MarkdownText.Code(null).Should().BeEmpty();
    }

    [Theory]
    [InlineData("`")]
    [InlineData("``` ` ``")]
    [InlineData("a`\n`b`")]
    public void Test_Code_NameCanNeverCloseTheSpan(string input)
    {
        var code = MarkdownText.Code(input);
        var fence = code.TakeWhile(c => c == '`').Count();
        var inner = code[fence..^fence];
        // The fence has to be longer than every backtick run inside it, or the span would end early.
        var longestInner = inner.Split(inner.Where(c => c != '`').Distinct().ToArray()).Select(r => r.Length).DefaultIfEmpty(0).Max();
        fence.Should().BeGreaterThan(longestInner);
    }

    [Theory]
    [InlineData("sudo chattr -i /x", "```\nsudo chattr -i /x\n```")]
    [InlineData("a```b", "````\na```b\n````")]
    [InlineData("one\r\ntwo\u0000", "```\none\ntwo\n```")]
    public void Test_CodeBlock_FenceIsLongerThanAnyBacktickRun(string input, string expected)
    {
        MarkdownText.CodeBlock(input).Should().Be(expected);
    }

    private sealed class Refusal(string message) : UserFacingException(message)
    {
        public override string Title => "Refused";
    }

    [Fact]
    public void Test_Message_LeavesARefusalAloneAndEscapesAnythingElse()
    {
        var refusal = new Refusal($"Couldn't read {MarkdownText.Code("my_mod")}");

        MarkdownText.Message(refusal).Should().Be("Couldn't read `my_mod`");
        MarkdownText.Message(new IOException("my_mod [x](http://e)")).Should().Be("my\\_mod [x]" + Z + "(http://e)");
    }

    [Fact]
    public void Test_Message_EscapingAMessageTwiceIsAvoided()
    {
        var once = MarkdownText.Message(new IOException("a_b"));

        MarkdownText.Message(new Refusal(once)).Should().Be(once);
    }

    [Theory]
    [InlineData("my_mod")]
    [InlineData("**bold** ~~x~~")]
    [InlineData("C:\\mods\\x")]
    [InlineData("[x](file:///etc/passwd)")]
    [InlineData("# heading")]
    [InlineData("a`b")]
    public void Test_ToPlain_UndoesInlineExceptForBackticks(string text)
    {
        MarkdownText.ToPlain(MarkdownText.Inline(text)).Should().Be(text.Replace('`', '\u02CB'));
    }

    [Theory]
    [InlineData("my_mod")]
    [InlineData("`lead")]
    [InlineData("trail`")]
    [InlineData("a``b")]
    [InlineData("C:\\x\\_y")]
    public void Test_ToPlain_ShowsACodeSpanAsItsText(string name)
    {
        MarkdownText.ToPlain($"{MarkdownText.Code(name)} is still running: {MarkdownText.Inline("a_b")}")
            .Should().Be($"{name} is still running: a_b");
    }

    [Fact]
    public void Test_Inline_ATrailingBackslashDoesNotHideTheNextCodeSpan()
    {
        // The renderer does not open a span right after a backslash.
        var paragraph = MarkdownText.Inline("C:\\mods\\") + MarkdownText.Code("[x](https://evil.example)");
        var spans = System.Text.RegularExpressions.Regex.Matches(paragraph, @"(?<!\\)(`+)(.+?)(?<!`)\1(?!`)", System.Text.RegularExpressions.RegexOptions.Singleline);

        spans.Should().ContainSingle();
        MarkdownText.ToPlain(paragraph).Should().Be("C:\\mods\\[x](https://evil.example)");
    }

    [Fact]
    public void Test_InlineKeepingCode_EscapesOutsideTextAndShowsSpansExactly()
    {
        var text = "The provided library item `my_mod` is bad_ [x](http://e) `a_b`";

        MarkdownText.InlineKeepingCode(text).Should().Be("The provided library item `my_mod` is bad\\_ [x]" + Z + "(http://e) `a_b`");
    }

    [Fact]
    public void Test_InlineKeepingCode_AStrayBacktickIsNeutralised()
    {
        MarkdownText.InlineKeepingCode("a `b` c` d").Should().Be("a `b` c\u02CB d");
    }
}
