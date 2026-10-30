using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Pages.MyGames;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;

namespace SilverModManager.UI.Tests;

public class MassRemovalPromptTests(IServiceProvider provider)
{
    private static readonly MassRemoval Removal = new(FileCount: 1235, DeployedCount: 1235, [new MassRemovalReason("Ultimate Mod List", 1235)], Unexplained: 0);

    [Fact]
    public void TheBodyNamesTheSwitchedOffCollectionAndItsFiles()
    {
        var prompt = MassRemovalPrompt.For("The Witcher 3", Removal);

        prompt.Markdown.Should().Contain("Ultimate Mod List is switched off, so applying removes its 1235 files from the game folder")
            .And.Contain("The Witcher 3").And.Contain("100%");
        prompt.Markdown.Should().NotContain("`").And.NotContain("silvermod");
    }

    [Fact]
    public void TheTitleIsShortAndCarriesNoNumbers()
    {
        var prompt = MassRemovalPrompt.For("The Witcher 3", Removal);

        prompt.Title.Length.Should().BeLessThan(48);
        prompt.Title.Should().NotMatchRegex("[0-9]").And.NotContain("The Witcher 3");
    }

    [Fact]
    public void CancelIsTheButtonEnterPresses()
    {
        var buttons = MassRemovalPrompt.For("The Witcher 3", Removal).Buttons;

        var cancel = buttons.Should().ContainSingle(b => b.Id == ButtonDefinitionId.Cancel).Subject;
        cancel.ButtonStyling.Should().Be(ButtonStyling.Primary);
        cancel.ButtonAction.Should().Be(ButtonAction.Accept, "Enter must never start a deletion of this size");
        buttons.Should().ContainSingle(b => b.Id == ButtonDefinitionId.Accept).Which.Text.Should().Be("Remove 1235 files");
    }

    [Fact]
    public void TheBodySaysWhatCancelDoes()
        => MassRemovalPrompt.For("X", Removal).Markdown.Should().Contain("Cancel").And.Contain("switch").And.Contain("back on");

    [Fact]
    public void FilesNoSwitchedOffItemAccountsForAreSaidToHaveNoOwner()
    {
        var prompt = MassRemovalPrompt.For("X", new MassRemoval(300, 500, [], Unexplained: 300));

        prompt.Markdown.Should().Contain("300 files no longer belong to anything in the loadout").And.NotContain("switched off");
    }

    [Fact]
    public void GuiContainer_ResolvesTheDialogPrompt()
        => provider.GetRequiredService<IMassRemovalPrompt>().Should().BeOfType<DialogMassRemovalPrompt>();
}
