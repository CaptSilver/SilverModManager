using NexusMods.Games.Bethesda.Loot.Metadata.Yaml;

namespace NexusMods.Games.Bethesda.Loot.Tests.Yaml;

// libloot's metadata_document.rs replace_prelude tests.
public class PreludeTests
{
    private const string NoteGlobals = "globals:\n  - type: note\n    content: A message.\n";

    [Fact]
    public void ShouldReturnAnEmptyStringIfGivenEmptyStrings()
    {
        var result = Prelude.Replace("", "");

        result.Masterlist.Should().BeEmpty();
        result.Diff.Should().BeNull();
    }

    [Fact]
    public void ShouldNotChangeAMasterlistWithNoPrelude()
    {
        var masterlist = "plugins:\n  - name: a.esp\n";

        var result = Prelude.Replace(masterlist, NoteGlobals);

        result.Masterlist.Should().Be(masterlist);
        result.Diff.Should().BeNull();
    }

    [Fact]
    public void ShouldNotChangeAFlowStyleMasterlist()
    {
        var masterlist = "{prelude: {}, plugins: [{name: a.esp}]}";

        var result = Prelude.Replace(masterlist, "globals: [{type: note, content: A message.}]");

        result.Masterlist.Should().Be(masterlist);
        result.Diff.Should().BeNull();
    }

    [Fact]
    public void ShouldReplaceAPreludeAtTheStartOfTheMasterlist()
    {
        var result = Prelude.Replace("prelude:\n  a: b\n\nplugins:\n  - name: a.esp\n", NoteGlobals);

        result.Masterlist.Should().Be("prelude:\n  globals:\n    - type: note\n      content: A message.\n\nplugins:\n  - name: a.esp\n");
        result.Diff.Should().Be(new PreludeDiffSpan(StartLine: 2, EndLine: 5, LinesAddedCount: 2));
    }

    [Fact]
    public void ShouldReplaceAPreludeWithNoLineBreak()
    {
        var result = Prelude.Replace("prelude:\n  a: b\nplugins:\n  - name: a.esp\n", NoteGlobals);

        result.Masterlist.Should().Be("prelude:\n  globals:\n    - type: note\n      content: A message.\n\nplugins:\n  - name: a.esp\n");
        result.Diff.Should().Be(new PreludeDiffSpan(2, 5, 3));
    }

    [Fact]
    public void ShouldReplaceAPreludeWithOneThatHasNoTrailingLineBreak()
    {
        var result = Prelude.Replace(
            "prelude:\n  a: b\n\nplugins:\n  - name: a.esp\n",
            "globals:\n  - type: note\n    content: A message.");

        result.Masterlist.Should().Be("prelude:\n  globals:\n    - type: note\n      content: A message.\nplugins:\n  - name: a.esp\n");
        result.Diff.Should().Be(new PreludeDiffSpan(2, 4, 1));
    }

    [Fact]
    public void ShouldReplaceAPreludeWithNoLineBreakWithOneThatHasNoTrailingLineBreak()
    {
        var result = Prelude.Replace(
            "prelude:\n  a: b\nplugins:\n  - name: a.esp\n",
            "globals:\n  - type: note\n    content: A message.");

        result.Masterlist.Should().Be("prelude:\n  globals:\n    - type: note\n      content: A message.\nplugins:\n  - name: a.esp\n");
        result.Diff.Should().Be(new PreludeDiffSpan(2, 4, 2));
    }

    [Fact]
    public void ShouldChangeAMasterlistThatEndsWithAPrelude()
    {
        var result = Prelude.Replace("plugins:\n  - name: a.esp\nprelude:\n  a: b\n\n", NoteGlobals);

        result.Masterlist.Should().Be("plugins:\n  - name: a.esp\nprelude:\n  globals:\n    - type: note\n      content: A message.\n");
        result.Diff.Should().Be(new PreludeDiffSpan(4, 7, 1));
    }

    [Fact]
    public void ShouldReplaceOnlyThePreludeInTheMasterlist()
    {
        var result = Prelude.Replace(
            "\ncommon:\n  key: value\nprelude:\n  a: b\nplugins:\n  - name: a.esp\n",
            "\n\nglobals:\n  - type: note\n    content: A message.\n\n");

        result.Masterlist.Should().Be("\ncommon:\n  key: value\nprelude:\n\n\n  globals:\n    - type: note\n      content: A message.\n\n\nplugins:\n  - name: a.esp\n");
        result.Diff.Should().Be(new PreludeDiffSpan(5, 11, 6));
    }

    [Fact]
    public void ShouldSucceedGivenABlockStylePreludeAndMasterlist()
    {
        var result = Prelude.Replace("prelude:\n  a: b\n\nplugins:\n  - name: a.esp\n", NoteGlobals);

        result.Masterlist.Should().Be("prelude:\n  globals:\n    - type: note\n      content: A message.\n\nplugins:\n  - name: a.esp\n");
        result.Diff.Should().Be(new PreludeDiffSpan(2, 5, 2));
    }

    [Fact]
    public void ShouldSucceedGivenAFlowStylePreludeAndABlockStyleMasterlist()
    {
        var result = Prelude.Replace(
            "prelude:\n  a: b\n\nplugins:\n  - name: a.esp\n",
            "globals: [{type: note, content: A message.}]");

        result.Masterlist.Should().Be("prelude:\n  globals: [{type: note, content: A message.}]\nplugins:\n  - name: a.esp\n");
        result.Diff.Should().Be(new PreludeDiffSpan(2, 2, -1));
    }

    [Fact]
    public void ShouldNotStopAtComments()
    {
        var result = Prelude.Replace("prelude:\n  a: b\n# Comment line\n  c: d\n\nplugins:\n  - name: a.esp\n", NoteGlobals);

        result.Masterlist.Should().Be("prelude:\n  globals:\n    - type: note\n      content: A message.\n\nplugins:\n  - name: a.esp\n");
        result.Diff.Should().Be(new PreludeDiffSpan(2, 5, 0));
    }

    [Fact]
    public void ShouldNotStopAtABlankLine()
    {
        var result = Prelude.Replace("prelude:\n  a: b\n\n\nplugins:\n  - name: a.esp\n", NoteGlobals);

        result.Masterlist.Should().Be("prelude:\n  globals:\n    - type: note\n      content: A message.\n\nplugins:\n  - name: a.esp\n");
        result.Diff.Should().Be(new PreludeDiffSpan(2, 5, 1));
    }

    [Fact]
    public void ShouldNotAddTrailingSpacesBeforeCarriageReturnsWhenIndentingACrlfPrelude()
    {
        var result = Prelude.Replace(
            "prelude:\r\n  a: b\r\nglobals: []",
            "common:\r\n  - &ref\r\n    type: say\r\n");

        result.Masterlist.Should().Be("prelude:\n  common:\r\n    - &ref\r\n      type: say\r\n\nglobals: []");
    }

    [Fact]
    public void ShouldOnlyRecogniseAPreludeKeyAtColumnZero()
    {
        var masterlist = "plugins:\n  - name: a.esp\n    prelude: x\n";

        Prelude.Replace(masterlist, NoteGlobals).Diff.Should().BeNull();
    }
}
