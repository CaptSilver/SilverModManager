using FluentAssertions;
using NSubstitute;
using SilverModManager.Abstractions.Library;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Pages.LibraryPage.Collections;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Tests.Pages.Collections;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>The one dialog that removes a modlist from the library, whoever asks for it.</summary>
public class WabbajackModlistRemoverTests(IServiceProvider provider) : AUiTest(provider)
{
    private readonly IWindowManager _windows = Substitute.For<IWindowManager>();
    private readonly IWorkspaceController _workspace = Substitute.For<IWorkspaceController>();
    private readonly List<Dialog> _shown = [];

    private WabbajackModlistRemover Remover => new(Provider);

    private void Answer(ButtonDefinitionId? pressed)
    {
        _windows.ShowDialog(Arg.Do<IDialog>(d => _shown.Add((Dialog)d)), Arg.Any<DialogWindowType>())
            .Returns(pressed is { } id ? new StandardDialogResult { ButtonId = id } : new StandardDialogResult());
    }

    private async Task<(LibraryFileId List, EntityId Owned)> ListWithAnOwnedArchive()
    {
        var file = await CollectionSeed.AddModlistFile(Connection, $"Remover list {Guid.NewGuid():N}");
        var owned = await CollectionSeed.AddOwnedArchive(Connection, file.Id, $"owned-{Guid.NewGuid():N}.7z");
        return (LibraryFileId.From(file.Id), owned);
    }

    private bool ListExists(LibraryFileId list) => WabbajackModlistLibraryFile.Load(Connection.Db, list.Value).IsValid();
    private bool FileExists(EntityId file) => LibraryFile.Load(Connection.Db, file).IsValid();

    [Fact]
    public async Task CancellingRemovesNothing()
    {
        var (list, owned) = await ListWithAnOwnedArchive();
        Answer(ButtonDefinitionId.Cancel);

        await Remover.ConfirmAndRemove(_windows, _workspace, list);

        ListExists(list).Should().BeTrue();
        FileExists(owned).Should().BeTrue();
    }

    [Fact]
    public async Task ClosingTheDialogRemovesNothing()
    {
        var (list, owned) = await ListWithAnOwnedArchive();
        Answer(null);

        await Remover.ConfirmAndRemove(_windows, _workspace, list);

        ListExists(list).Should().BeTrue();
        FileExists(owned).Should().BeTrue();
    }

    [Fact]
    public async Task AcceptingRemovesTheListAndKeepsItsArchives()
    {
        var (list, owned) = await ListWithAnOwnedArchive();
        Answer(ButtonDefinitionId.Accept);

        await Remover.ConfirmAndRemove(_windows, _workspace, list);

        ListExists(list).Should().BeFalse();
        FileExists(owned).Should().BeTrue();
    }

    [Fact]
    public async Task TheWithArchivesButtonAlsoRemovesTheArchivesOnlyThisListUses()
    {
        var (list, owned) = await ListWithAnOwnedArchive();
        Answer(WabbajackModlistRemover.RemoveWithArchives);

        await Remover.ConfirmAndRemove(_windows, _workspace, list);

        ListExists(list).Should().BeFalse();
        FileExists(owned).Should().BeFalse();
    }

    [Fact]
    public async Task TheDialogIsTitledWithTheListsNameAndOffersTheArchivesOnlyWhenThereAreSome()
    {
        var (list, _) = await ListWithAnOwnedArchive();
        var bare = await CollectionSeed.AddModlistFile(Connection, $"Bare list {Guid.NewGuid():N}");
        Answer(ButtonDefinitionId.Cancel);

        await Remover.ConfirmAndRemove(_windows, _workspace, list);
        await Remover.ConfirmAndRemove(_windows, _workspace, LibraryFileId.From(bare.Id));

        _shown[0].ViewModel.WindowTitle.Should().StartWith("Remove 'Remover list ").And.EndWith("' from the library?");
        _shown[0].ViewModel.ButtonDefinitions.Select(b => b.Text).Should().Equal("Cancel", "Remove list, keep archives", "Remove list and 1 archives (1000 B)");
        _shown[1].ViewModel.ButtonDefinitions.Select(b => b.Text).Should().Equal("Cancel", "Remove");
    }

    [Fact]
    public async Task TheTextNamesTheLoadoutsTheListIsInstalledIn()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Remover loadout {Guid.NewGuid():N}");
        var group = await CollectionSeed.AddModlistGroup(Connection, loadout.LoadoutId, $"Installed list {Guid.NewGuid():N}");
        var plan = Remover.Plan(LibraryFileId.From(group.ModlistId.Value));

        WabbajackModlistRemover.RemovalText(plan).Should().Contain(loadout.Name);
    }

    [Fact]
    public async Task ABareListSaysOnlyThatTheFileIsRemoved()
    {
        var file = await CollectionSeed.AddModlistFile(Connection, $"Bare text {Guid.NewGuid():N}");

        WabbajackModlistRemover.RemovalText(Remover.Plan(LibraryFileId.From(file.Id)))
            .Should().Be("The modlist file is removed from the library.");
    }
}
