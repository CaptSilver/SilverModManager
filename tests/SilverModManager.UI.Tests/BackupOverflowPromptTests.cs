using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Pages.MyGames;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;

namespace SilverModManager.UI.Tests;

public class BackupOverflowPromptTests(IServiceProvider provider)
{
    private static readonly BackupOverflow Overflow = new(Adoptable: Size.From(15_748_000_000), AdoptableCount: 4506, MustBackUp: Size.Zero, MustBackUpCount: 0, Limit: Size.GB * 5, Operation: BackupOverflowOperation.Manage);

    [Fact]
    public void WhenAdoptIsNotPossible_SaysTheFilesWouldBeDeleted()
    {
        var prompt = BackupOverflowPrompt.For("The Witcher 3", Overflow with { Adoptable = Size.Zero, AdoptableCount = 0, MustBackUp = Size.GB * 6, MustBackUpCount = 10 });

        prompt.Markdown.Should().Contain("deleted").And.NotContain("Adopt");
        prompt.Buttons.Select(b => b.Text).Should().NotContain("Adopt in place");
    }

    [Fact]
    public void TitleNamesTheGameCountAndSize()
        => BackupOverflowPrompt.For("The Witcher 3", Overflow).Title.Should().Contain("The Witcher 3").And.Contain("4506").And.Contain("14.7 GB").And.NotContain("14.666");

    [Fact]
    public void MarkdownSaysWhenAdoptedFilesGetBackedUpAndNamesTheBackupVerb()
    {
        var md = BackupOverflowPrompt.For("The Witcher 3", Overflow).Markdown;
        md.Should().Contain("switching loadouts").And.Contain("backup-game-files").And.Contain("5.0 GB").And.NotContain("cannot restore them");
    }

    [Fact]
    public void MixedOverflow_SaysWhichPartIsBackedUpEitherWay()
        => BackupOverflowPrompt.For("X", Overflow with { MustBackUp = Size.KB * 12, MustBackUpCount = 2 }).Markdown.Should().Contain("2 of them").And.Contain("either way");

    [Fact]
    public void ButtonsOfferAdoptBackUpAndDontManage()
    {
        var buttons = BackupOverflowPrompt.For("The Witcher 3", Overflow).Buttons;

        buttons.Select(b => b.Text).Should().Equal("Adopt in place", "Back up 14.7 GB", "Don't manage");
    }

    [Fact]
    public void SizesReadTheWayTheRestOfTheAppShowsThem()
    {
        var md = BackupOverflowPrompt.For("The Witcher 3", Overflow).Markdown;

        md.Should().Contain("5.0 GB").And.Contain("14.7 GB").And.NotContain("14.666");
        BackupOverflow.FormatSize(Size.From(15_748_000_000)).Should().Be("14.7 GB");
        BackupOverflow.FormatSize(Size.From(12_288)).Should().Be("12 KB");
    }

    [Theory]
    [InlineData(BackupOverflowOperation.Manage, "Don't manage")]
    [InlineData(BackupOverflowOperation.Apply, "Don't apply")]
    [InlineData(BackupOverflowOperation.Launch, "Cancel")]
    [InlineData(BackupOverflowOperation.Switch, "Cancel")]
    [InlineData(BackupOverflowOperation.Reset, "Cancel")]
    public void CancelButtonNamesWhatItStops(BackupOverflowOperation operation, string label)
    {
        var buttons = BackupOverflowPrompt.For("The Witcher 3", Overflow with { Operation = operation }).Buttons;

        buttons.Last().Text.Should().Be(label);
        buttons.Last().Id.Should().Be(ButtonDefinitionId.Cancel);
    }

    [Theory]
    [InlineData(BackupOverflowOperation.Manage, "added files")]
    [InlineData(BackupOverflowOperation.Apply, "added files")]
    [InlineData(BackupOverflowOperation.Launch, "added files")]
    [InlineData(BackupOverflowOperation.Switch, "switching loadouts")]
    [InlineData(BackupOverflowOperation.Reset, "putting the folder back")]
    public void TitleNamesTheOperation(BackupOverflowOperation operation, string phrase)
        => BackupOverflowPrompt.For("The Witcher 3", Overflow with { Operation = operation }).Title.Should().Contain(phrase);

    [Fact]
    public void OnlyBackUpIsTheEnterKeyDefault()
    {
        var buttons = BackupOverflowPrompt.For("The Witcher 3", Overflow).Buttons;

        buttons.Where(b => b.ButtonAction == ButtonAction.Accept).Select(b => b.Text).Should().Equal("Back up 14.7 GB");
    }

    [Fact]
    public void GuiContainer_ResolvesTheDialogPrompt()
        => provider.GetRequiredService<IBackupOverflowPrompt>().Should().BeOfType<DialogBackupOverflowPrompt>();
}
