using FluentAssertions;
using SilverModManager.App.UI.Notifications;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.UI.Tests.Notifications;

public class ToastTextTests
{
    [Fact]
    public void ANameWrappedByMarkdownTextCodeShowsWithoutBackticks()
    {
        var notification = WindowNotificationService.BuildNotification($"The revision of {MarkdownText.Code("Yggdrasil VR")} is gone.", null);

        notification.Message.Should().Be("The revision of Yggdrasil VR is gone.");
    }

    [Theory]
    [InlineData("[x](file:///etc/passwd)")]
    [InlineData("`](http://evil.example)`")]
    [InlineData("a`b")]
    [InlineData("line one\n[y](nxm://skyrim/mods/1/files/2)")]
    [InlineData("```\n[x](http://evil.example)\n```")]
    public void AHostileNameReachesTheToastAsItsOwnText(string name)
    {
        var notification = WindowNotificationService.BuildNotification($"Cannot update {MarkdownText.Code(name)} now.", null);

        notification.Message.Should().Be($"Cannot update {MarkdownText.PlainText(name)} now.");
    }

    [Theory]
    [InlineData("a\\_b")]
    [InlineData("*stars* and ~tildes~")]
    public void ARawNameEscapedForMarkdownShowsUnchanged(string raw)
    {
        var notification = WindowNotificationService.BuildNotification($"Couldn't update {MarkdownText.InlineMidLine(raw)} to 2: {MarkdownText.Code("x")} is gone", null);

        notification.Message.Should().Be($"Couldn't update {raw} to 2: x is gone");
    }

    // InlineMidLine swaps a backtick for a lookalike on purpose: a stray one in a raw name must not pair with the
    // code span that follows it in the same message and swallow the text between them.
    [Fact]
    public void AStrayBacktickInARawNameShowsAsALookalikeAndLeavesTheRestOfTheToastAlone()
    {
        var notification = WindowNotificationService.BuildNotification($"Couldn't update {MarkdownText.InlineMidLine("Bob`s List")} to 2: {MarkdownText.Code("x")} is gone", null);

        notification.Message.Should().Be("Couldn't update Bobˋs List to 2: x is gone");
    }
}
