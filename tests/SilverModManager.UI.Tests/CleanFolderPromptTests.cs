using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Pages.MyGames;

namespace SilverModManager.UI.Tests;

public class CleanFolderPromptTests
{
    [Fact]
    public void CleanFolderPromptOffersNoCleanForAnUncoveredBuild()
    {
        var prompt = CleanFolderPrompt.For(buildCovered: false, fileCount: 319, gameName: "Skyrim Special Edition", unbacked: default);

        prompt.OffersClean.Should().BeFalse();
        prompt.Title.Should().Be("Skyrim Special Edition: this build isn't recognised yet");
        prompt.Markdown.Should().Contain("can't tell the game's own files from added ones");
        prompt.Markdown.Should().Contain("will not delete them when you clean or remove the game");
        prompt.Markdown.Should().NotContain("319", "the count is of files the app cannot classify");
    }

    [Fact]
    public void CleanFolderPromptIsUnchangedForACoveredBuild()
    {
        var prompt = CleanFolderPrompt.For(buildCovered: true, fileCount: 7, gameName: "Cyberpunk 2077", unbacked: default);

        prompt.OffersClean.Should().BeTrue();
        prompt.Title.Should().Be("Your Cyberpunk 2077 folder isn't a clean install");
        prompt.Markdown.Should().Contain("We found 7 files in the game folder that aren’t part of a clean install");
        prompt.Markdown.Should().Contain("we recommend starting with a clean folder");
    }

    [Fact]
    public void CoveredBuild_NamesTheTopFoldersItWillMoveWithTheirFileCounts()
    {
        var files = Enumerable.Range(0, 48).Select(i => $"Phoenix/Content/Paks/~mods/Mod-{i / 16}/{i}.pak")
            .Append("UE4SS.log").Append("crash/a.dmp").Append("crash/b.dmp").ToArray();

        var prompt = CleanFolderPrompt.For(buildCovered: true, files.Length, "Hogwarts Legacy", default, files);

        prompt.Markdown.Should().Contain("51 files");
        var lines = prompt.Markdown.Split('\n').Select(line => line.TrimEnd()).ToArray();
        lines.Should().Contain("- Phoenix/Content/Paks/~mods/Mod-0 (16 files)");
        lines.Should().Contain("- Phoenix/Content/Paks/~mods/Mod-1 (16 files)");
        lines.Should().Contain("- Phoenix/Content/Paks/~mods/Mod-2 (16 files)");
        lines.Should().Contain("- crash (2 files)");
        lines.Should().Contain("- UE4SS.log");
        lines.Should().NotContain("- Phoenix (48 files)");
    }

    [Fact]
    public void CoveredBuild_FollowsAFolderThatHoldsOnlyOneSubfolderDownToTheModsFolder()
    {
        var files = new[] { "Phoenix/Content/Paks/~mods/Only/a.pak", "Phoenix/Content/Paks/~mods/Only/sub/b.pak" };

        var prompt = CleanFolderPrompt.For(buildCovered: true, files.Length, "Hogwarts Legacy", default, files);

        prompt.Markdown.Should().Contain("- Phoenix/Content/Paks/~mods/Only (2 files)");
    }

    [Fact]
    public void CoveredBuild_StopsAtAFolderThatHoldsLooseFilesBesideASubfolder()
    {
        var files = new[] { "Mods/readme.txt", "Mods/A/a.pak", "Mods/B/b.pak" };

        var prompt = CleanFolderPrompt.For(buildCovered: true, files.Length, "X", default, files);

        prompt.Markdown.Should().Contain("- Mods (3 files)");
    }

    [Fact]
    public void CoveredBuild_WithOneFile_SaysFileNotFiles()
    {
        var prompt = CleanFolderPrompt.For(buildCovered: true, 1, "X", default, ["a.pak"]);

        prompt.Markdown.Should().Contain("We found 1 file in the game folder").And.NotContain("1 files");
    }

    [Fact]
    public void CoveredBuild_SaysCleanFolderTakesTheFilesOutNow_NotOnTheNextApply()
    {
        var prompt = CleanFolderPrompt.For(buildCovered: true, 3, "X", default, ["a.pak", "b.pak", "c.pak"]);

        prompt.Markdown.Should().Contain("Clean folder takes them out of the game folder now; switch the group back on to put them back.");
        prompt.Markdown.Should().NotContain("next Apply");
    }

    [Fact]
    public void CoveredBuild_WhenEveryChangeIsAMissingGameFile_LeadsWithThemAndPromisesNoMove()
    {
        var prompt = CleanFolderPrompt.For(buildCovered: true, 0, "Hogwarts Legacy", default, [], missingGameFiles: 3);

        prompt.OffersClean.Should().BeTrue();
        prompt.Markdown.Should().StartWith("3 files the game ships are missing from the folder. **Clean folder** puts them back.");
        prompt.Markdown.Should().NotContain("We found").And.NotContain("0 files").And.NotContain("moves these files").And.NotContain("Found in the game folder");
    }

    [Fact]
    public void CoveredBuild_SaysTheFilesMoveToASwitchedOffGroupInsteadOfBeingDeleted()
    {
        var prompt = CleanFolderPrompt.For(buildCovered: true, 3, "Hogwarts Legacy", default, ["a.pak", "b.pak", "c.pak"]);

        prompt.Markdown.Should().Contain("Found in the game folder").And.Contain("My Mods").And.Contain("switched off");
        prompt.Markdown.Should().Contain("switch").And.Contain("back on");
        prompt.Markdown.Should().NotContain("deletes");
    }

    [Fact]
    public void CoveredBuild_ListsAtMostAHandfulOfFoldersAndCountsTheRest()
    {
        var files = Enumerable.Range(0, 30).Select(i => $"Folder{i:00}/f.txt").ToArray();

        var prompt = CleanFolderPrompt.For(buildCovered: true, files.Length, "X", default, files);

        prompt.Markdown.Should().Contain("and 22 more");
    }

    [Fact]
    public void CleanFolderPromptSaysContentTheGameInstalledIsKept()
    {
        var prompt = CleanFolderPrompt.For(buildCovered: true, fileCount: 7, gameName: "Skyrim Special Edition", unbacked: default);

        prompt.Markdown.Should().Contain("installed itself");
        prompt.Markdown.Should().Contain("is kept");
    }

    [Fact]
    public void CoveredBuildWithUnbackedFiles_SaysTheyAreCopiedFirst()
    {
        var prompt = CleanFolderPrompt.For(buildCovered: true, fileCount: 10, "The Witcher 3", new UnbackedFiles(4, Size.GB * 6));

        prompt.Markdown.Should().Contain("4 of them").And.Contain("6 GB").And.Contain("copies them").And.NotContain("cannot restore");
    }

    [Fact]
    public void NoUnbackedFiles_SaysNothingAboutBackups()
        => CleanFolderPrompt.For(true, 10, "X", default).Markdown.Should().NotContain("backup copy");

    [Fact]
    public void RemoveGameNote_SaysTheFilesAreDeletedForGood_NotBackedUp()
    {
        var note = CleanFolderPrompt.RemoveGameNote(new UnbackedFiles(4, Size.GB * 6));

        note.Should().Contain("4 files").And.Contain("6 GB").And.Contain("no backup copy")
            .And.Contain("deletes them").And.NotContain("backup first");
    }

    [Fact]
    public void CoveredBuild_SaysTheGamesMissingFilesAreRestoredAndKeepsThemOutOfTheMovedList()
    {
        var prompt = CleanFolderPrompt.For(buildCovered: true, 1, "Hogwarts Legacy", default, ["UE4SS.log"], missingGameFiles: 3);

        prompt.Markdown.Should().Contain("We found 1 file in the game folder");
        prompt.Markdown.Should().Contain("3 files the game ships are missing from the folder. **Clean folder** puts them back.");
    }

    [Fact]
    public void CoveredBuild_WithNoMissingGameFilesSaysNothingAboutRestoring()
    {
        var prompt = CleanFolderPrompt.For(buildCovered: true, 1, "Hogwarts Legacy", default, ["UE4SS.log"]);

        prompt.Markdown.Should().NotContain("puts them back");
    }
}
