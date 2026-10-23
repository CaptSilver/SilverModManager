using FluentAssertions;
using NexusMods.Sdk.IO;

namespace NexusMods.Sdk.Tests.IO;

public class FolderNameTests
{
    [Theory]
    [InlineData("Mod", true)]
    [InlineData("My Mod 2", true)]
    [InlineData("a.b", true)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("...", false)]
    [InlineData("x.", false)]
    [InlineData("x ", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("a:b", false)]
    [InlineData("a<b>c", false)]
    [InlineData("a\"b", false)]
    [InlineData("a|b", false)]
    [InlineData("a?b", false)]
    [InlineData("a*b", false)]
    [InlineData("a\tb", false)]
    [InlineData("a\0b", false)]
    public void IsUsableAcceptsOnlyASingleSafeFolderName(string name, bool expected) =>
        FolderName.IsUsable(name).Should().Be(expected);

    [Theory]
    [InlineData("a/b", "a_b")]
    [InlineData("a\\b", "a_b")]
    [InlineData("C:evil", "C_evil")]
    [InlineData("x.", "x")]
    [InlineData("x ", "x")]
    [InlineData("x. .", "x")]
    [InlineData("a\tb", "a_b")]
    [InlineData("a\0b", "a_b")]
    [InlineData("a<>:\"|?*b", "a_______b")]
    [InlineData("Fine Name", "Fine Name")]
    public void SanitizeReplacesForbiddenCharactersAndTrimsTrailingDotsAndSpaces(string name, string expected) =>
        FolderName.Sanitize(name).Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData(". .")]
    public void SanitizeGivesNullWhenNothingUsableIsLeft(string name) =>
        FolderName.Sanitize(name).Should().BeNull();

    [Theory]
    [InlineData("a/b")]
    [InlineData("x.")]
    [InlineData("a\tb")]
    public void ASanitizedNameIsAlwaysUsable(string name) =>
        FolderName.IsUsable(FolderName.Sanitize(name)!).Should().BeTrue();
}
