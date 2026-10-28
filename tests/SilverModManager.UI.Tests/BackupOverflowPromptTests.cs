using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Pages.MyGames;

namespace SilverModManager.UI.Tests;

public class BackupOverflowPromptTests(IServiceProvider provider)
{
    private static readonly BackupOverflow Overflow = new(Adoptable: Size.From(15_748_000_000), AdoptableCount: 4506, MustBackUp: Size.Zero, MustBackUpCount: 0, Limit: Size.GB * 5);

    [Fact]
    public void WhenAdoptIsNotPossible_SaysTheFilesWouldBeDeleted()
    {
        var prompt = BackupOverflowPrompt.For("The Witcher 3", Overflow with { Adoptable = Size.Zero, AdoptableCount = 0, MustBackUp = Size.GB * 6, MustBackUpCount = 10 });

        prompt.Markdown.Should().Contain("deleted").And.NotContain("Adopt");
        prompt.Buttons.Select(b => b.Text).Should().NotContain("Adopt in place");
    }

    [Fact]
    public void TitleNamesTheGameCountAndSize()
        => BackupOverflowPrompt.For("The Witcher 3", Overflow).Title.Should().Contain("The Witcher 3").And.Contain("4506").And.Contain("14.666 GB");

    [Fact]
    public void MarkdownSaysWhenAdoptedFilesGetBackedUpAndNamesTheBackupVerb()
    {
        var md = BackupOverflowPrompt.For("The Witcher 3", Overflow).Markdown;
        md.Should().Contain("switching loadouts").And.Contain("backup-game-files").And.Contain("5 GB").And.NotContain("cannot restore them");
    }

    [Fact]
    public void MixedOverflow_SaysWhichPartIsBackedUpEitherWay()
        => BackupOverflowPrompt.For("X", Overflow with { MustBackUp = Size.KB * 12, MustBackUpCount = 2 }).Markdown.Should().Contain("2 of them").And.Contain("either way");

    [Fact]
    public void ButtonsOfferAdoptBackUpAndDontManage()
    {
        var buttons = BackupOverflowPrompt.For("The Witcher 3", Overflow).Buttons;

        buttons.Select(b => b.Text).Should().Equal("Adopt in place", "Back up 14.666 GB", "Don't manage");
    }

    [Fact]
    public void GuiContainer_ResolvesTheDialogPrompt()
        => provider.GetRequiredService<IBackupOverflowPrompt>().Should().BeOfType<DialogBackupOverflowPrompt>();
}
