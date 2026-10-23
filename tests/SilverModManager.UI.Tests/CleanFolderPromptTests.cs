using FluentAssertions;
using SilverModManager.App.UI.Pages.MyGames;

namespace SilverModManager.UI.Tests;

public class CleanFolderPromptTests
{
    [Fact]
    public void CleanFolderPromptOffersNoCleanForAnUncoveredBuild()
    {
        var prompt = CleanFolderPrompt.For(buildCovered: false, fileCount: 319, gameName: "Skyrim Special Edition");

        prompt.OffersClean.Should().BeFalse();
        prompt.Title.Should().Be("Skyrim Special Edition: this build isn't recognised yet");
        prompt.Markdown.Should().Contain("can't tell the game's own files from added ones");
        prompt.Markdown.Should().Contain("will not delete them when you clean or remove the game");
        prompt.Markdown.Should().NotContain("319", "the count is of files the app cannot classify");
    }

    [Fact]
    public void CleanFolderPromptIsUnchangedForACoveredBuild()
    {
        var prompt = CleanFolderPrompt.For(buildCovered: true, fileCount: 7, gameName: "Cyberpunk 2077");

        prompt.OffersClean.Should().BeTrue();
        prompt.Title.Should().Be("Your Cyberpunk 2077 folder isn't a clean install");
        prompt.Markdown.Should().Contain("We found 7 files in the game folder that aren’t part of a clean install");
        prompt.Markdown.Should().Contain("we recommend starting with a clean folder");
    }

    [Fact]
    public void CleanFolderPromptSaysContentTheGameInstalledIsKept()
    {
        var prompt = CleanFolderPrompt.For(buildCovered: true, fileCount: 7, gameName: "Skyrim Special Edition");

        prompt.Markdown.Should().Contain("installed itself");
        prompt.Markdown.Should().Contain("is kept");
    }
}
