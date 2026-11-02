using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Avalonia;
using SilverModManager.Abstractions.Loadouts;
using Size = NexusMods.Paths.Size;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Collections;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.App.UI.Pages.Collections;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Icons;
using SilverModManager.Wabbajack.Models;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Pages.Collections;

public class WabbajackModlistSourceTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed record Fixture(Loadout.ReadOnly Loadout, WabbajackModlistLibraryFile.ReadOnly File, WabbajackModlistSource Source)
    {
        public LibraryFileId FileId => LibraryFileId.From(File.Id);
    }

    private async Task<Fixture> Open(bool pinned = true, IWabbajackModlistService? service = null, IServiceProvider? services = null)
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Modlist source {Guid.NewGuid()}");
        var name = $"Source list {Guid.NewGuid():N}";
        var file = await CollectionSeed.AddModlistFile(Connection, name, version: "3.2.1", author: "A modder", archiveCount: 312, archiveSize: Size.GB * 48);
        var source = new WabbajackModlistSource(services ?? Provider, pinned ? loadout.LoadoutId : null, file, service);
        return new Fixture(loadout, file, source);
    }

    private async Task<EntityId> InstallGroup(Fixture f, bool finished = false, bool skipped = false)
    {
        var group = await WabbajackGroupSeed.AddGroup(Connection, f.File.Id, f.Loadout.LoadoutId, f.File.Name);
        if (finished) await WabbajackGroupSeed.Finish(Connection, group);
        if (skipped) await WabbajackGroupSeed.AddSkip(Connection, group, f.Loadout.LoadoutId);
        return group;
    }

    [Fact]
    public async Task ItsHeaderComesFromTheLibraryFileWithoutOpeningTheArchive()
    {
        var f = await Open();

        f.Source.InitialName.Should().Be(f.File.Name);
        (await f.Source.Name.FirstAsync()).Should().Be(f.File.Name);
        f.Source.AuthorName.Should().Be("A modder");
        f.Source.VersionText.Should().Be("Version 3.2.1");
        f.Source.Badge!.Text.Should().Be("Wabbajack");
        f.Source.IsReadOnly.Should().BeFalse();
        (await f.Source.TileImage.FirstAsync()).Should().BeNull("the banner is read when the page activates");
    }

    [Fact]
    public async Task ItsStatsAreTheDownloadSizeAndTheArchiveCountOnly()
    {
        var f = await Open();

        f.Source.Stats.Should().Be(new CollectionStats(null, null, null, Size.GB * 48, 312));
    }

    [Fact]
    public async Task TheStateLineAndInstalledFlagFollowTheThreeStates()
    {
        var f = await Open();
        (await f.Source.StateLine.FirstAsync()).Should().Be("Not installed yet");
        (await f.Source.IsInstalled.FirstAsync()).Should().BeFalse();
        (await f.Source.Group.FirstAsync()).HasValue.Should().BeFalse();

        var group = await InstallGroup(f);
        (await f.Source.StateLine.FirstAsync()).Should().Be("Install not finished");
        (await f.Source.IsInstalled.FirstAsync()).Should().BeFalse();
        (await f.Source.Group.FirstAsync()).Value.Value.Should().Be(group);

        await WabbajackGroupSeed.Finish(Connection, group);
        (await f.Source.StateLine.FirstAsync()).Should().Be(Language.CollectionLoadoutView_CollectionInstalled);
        (await f.Source.IsInstalled.FirstAsync()).Should().BeTrue();

        await WabbajackGroupSeed.AddSkip(Connection, group, f.Loadout.LoadoutId);
        (await f.Source.StateLine.FirstAsync()).Should().Be("Install not finished");
        (await f.Source.IsInstalled.FirstAsync()).Should().BeFalse("a finished install that skipped files is not complete");
    }

    [Fact]
    public async Task TheStateLineEmitsWhenTheInstallFinishes()
    {
        var f = await Open();
        var group = await InstallGroup(f);
        var lines = new List<string>();
        using var subscription = f.Source.StateLine.Subscribe(lines.Add);

        await WabbajackGroupSeed.Finish(Connection, group);

        await Eventually(() => lines.Should().Equal("Install not finished", Language.CollectionLoadoutView_CollectionInstalled));
    }

    [Fact]
    public async Task TheGroupCanBeSwitchedOnOnlyOnceTheInstallWroteItsEnd()
    {
        var f = await Open();
        (await f.Source.CanToggle.FirstAsync()).Should().BeFalse("no group yet");

        var group = await InstallGroup(f);
        (await f.Source.CanToggle.FirstAsync()).Should().BeFalse("the job is still writing the list");

        await WabbajackGroupSeed.Finish(Connection, group);
        (await f.Source.CanToggle.FirstAsync()).Should().BeTrue();

        await WabbajackGroupSeed.SetEnabled(Connection, group, false);
        (await f.Source.CanToggle.FirstAsync()).Should().BeTrue("switching a finished list off is the user's choice");
    }

    [Fact]
    public async Task ActivatingReadsTheBannerAndTheBlurbOnceEach()
    {
        var banner = await Dispatcher.UIThread.InvokeAsync(() => new WriteableBitmap(new PixelSize(4, 4), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul));
        var service = Substitute.For<IWabbajackModlistService>();
        service.Open(Arg.Any<LibraryFileId>(), Arg.Any<CancellationToken>()).Returns(call =>
            Task.FromResult(new WabbajackModlistDetails("n", "a", "v", "SkyrimSpecialEdition", null, 1, Size.Zero, Size.Zero, banner)));
        service.ReadBlurb(Arg.Any<LibraryFileId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new WabbajackModlistBlurb("A list about trees.", new Uri("https://example.com/readme"))));
        var f = await Open(service: service);

        using var activation = f.Source.Activate();

        await Eventually(async () =>
        {
            (await f.Source.TileImage.FirstAsync()).Should().BeSameAs(banner);
            (await f.Source.BackgroundImage.FirstAsync()).Should().BeSameAs(banner);
            (await f.Source.Description.FirstAsync()).Should().Be("A list about trees.");
        });
        _ = service.Received(1).Open(f.FileId, Arg.Any<CancellationToken>());
        _ = service.Received(1).ReadBlurb(f.FileId, Arg.Any<CancellationToken>());
        (await f.Source.Link!.IsVisible.FirstAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task TheReadmeLinkIsHiddenUntilTheListGivesOne()
    {
        var service = Substitute.For<IWabbajackModlistService>();
        service.Open(Arg.Any<LibraryFileId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new WabbajackModlistDetails("n", "a", "v", "SkyrimSpecialEdition", null, 1, Size.Zero, Size.Zero, null)));
        service.ReadBlurb(Arg.Any<LibraryFileId>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new WabbajackModlistBlurb("", null)));
        var f = await Open(service: service);

        f.Source.Link!.Text.Should().Be("Open readme");
        (await f.Source.Link.IsVisible.FirstAsync()).Should().BeFalse();
        using var activation = f.Source.Activate();
        await Eventually(() => { _ = service.Received().ReadBlurb(f.FileId, Arg.Any<CancellationToken>()); });
        (await f.Source.Link.IsVisible.FirstAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task AnEmptyBlurbGivesNoInstructions()
    {
        var service = Substitute.For<IWabbajackModlistService>();
        service.Open(Arg.Any<LibraryFileId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new WabbajackModlistDetails("n", "a", "v", "SkyrimSpecialEdition", null, 1, Size.Zero, Size.Zero, null)));
        service.ReadBlurb(Arg.Any<LibraryFileId>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new WabbajackModlistBlurb("", null)));
        var f = await Open(service: service);

        using var activation = f.Source.Activate();
        await Eventually(() => { _ = service.Received().ReadBlurb(f.FileId, Arg.Any<CancellationToken>()); });

        (await f.Source.Instructions.FirstAsync()).HasAny.Should().BeFalse();
    }

    [Fact]
    public async Task TheBlurbAndTheReadmeLineBecomeTheInstructions()
    {
        var service = Substitute.For<IWabbajackModlistService>();
        service.Open(Arg.Any<LibraryFileId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new WabbajackModlistDetails("n", "a", "v", "SkyrimSpecialEdition", null, 1, Size.Zero, Size.Zero, null)));
        service.ReadBlurb(Arg.Any<LibraryFileId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new WabbajackModlistBlurb("Mind the load order.", new Uri("https://example.com/readme"))));
        var f = await Open(service: service);

        using var activation = f.Source.Activate();

        await Eventually(async () =>
        {
            var instructions = await f.Source.Instructions.FirstAsync();
            instructions.HasAny.Should().BeTrue();
            instructions.Collection!.Contents.Should().Contain("Mind the load order.").And.Contain("Readme: https://example.com/readme");
        });
    }

    [Fact]
    public async Task ConstructionBuildsNoDownloadsModel()
    {
        var f = await Open();

        f.Source.Should().NotBeNull();
        var downloads = f.Source.CreateDownloads();

        downloads.Should().BeAssignableTo<IWabbajackModlistViewModel>();
        var model = (IWabbajackModlistViewModel)downloads!;
        model.FileId.Should().Be(f.FileId);
        model.PinnedLoadout.Should().Be(f.Loadout.LoadoutId);
    }

    [Fact]
    public async Task AnUnpinnedSourceLeavesTheDownloadsModelUnpinned()
    {
        var f = await Open(pinned: false);

        var model = (IWabbajackModlistViewModel)f.Source.CreateDownloads()!;

        model.PinnedLoadout.Should().BeNull();
        f.Source.LoadoutId.Should().BeNull();
    }

    [Fact]
    public async Task TheInstalledAndPendingPagesAreTheCardsInstallPage()
    {
        var f = await Open();
        var expected = new PageData
        {
            FactoryId = WabbajackModlistPageFactory.StaticId,
            Context = new WabbajackModlistPageContext { LibraryFileId = f.FileId, TargetLoadout = f.Loadout.LoadoutId },
        };

        f.Source.PendingPage.Should().Be(expected);
        f.Source.InstalledPage(CollectionGroupId.From(EntityId.From(1))).Should().Be(expected);
    }

    [Fact]
    public async Task TheMoreMenuIsViewDownloadsThenTheInboxAndFolderItems()
    {
        var f = await Open();
        f.Source.CreateDownloads();
        var viewDownloads = new ContextMenuItem { Header = "View downloads" };

        var items = f.Source.MenuItems(Substitute.For<ICollectionPageHost>(), viewDownloads);

        items.Select(item => item.Header).Should().Equal(
            "View downloads", "Check inbox", "Open inbox folder", "Choose download folder...", "Use the default download folder", "Reinstall modlist");
        items[0].Should().BeSameAs(viewDownloads);
    }

    [Fact]
    public async Task TheMoreMenuTakesTheModelsOwnCommands()
    {
        var f = await Open();
        var model = (IWabbajackModlistViewModel)f.Source.CreateDownloads()!;

        var items = f.Source.MenuItems(Substitute.For<ICollectionPageHost>(), null);

        items.Take(4).Select(item => item.Command).Should().Equal(
            model.CheckInboxCommand, model.OpenInboxFolderCommand, model.ChooseDownloadsFolderCommand, model.UseDefaultDownloadsFolderCommand);
    }

    [Fact]
    public async Task ReinstallSitsLastSoItIsNextToRemoveAndCarriesItsIconAndTooltip()
    {
        var f = await Open();
        f.Source.CreateDownloads();

        var reinstall = f.Source.MenuItems(Substitute.For<ICollectionPageHost>(), null)[^1];

        reinstall.Header.Should().Be("Reinstall modlist");
        reinstall.Icon.Should().Be(IconValues.Refresh);
        reinstall.ToolTip.Should().Be("Removes what the install put in the loadout and installs it again from the library. Nothing is downloaded.");
    }

    [Fact]
    public async Task ReinstallCanRunOnlyOnceTheListIsInstalledInTheLoadout()
    {
        var f = await Open();
        f.Source.CreateDownloads();
        var reinstall = f.Source.MenuItems(Substitute.For<ICollectionPageHost>(), null)[^1];
        await Eventually(async () => (await reinstall.Command.CanExecute.FirstAsync()).Should().BeFalse("nothing is installed yet"));

        var group = await InstallGroup(f, finished: true);
        await Eventually(async () => (await reinstall.Command.CanExecute.FirstAsync()).Should().BeTrue());

        await WabbajackGroupSeed.Delete(Connection, group);
        await Eventually(async () => (await reinstall.Command.CanExecute.FirstAsync()).Should().BeFalse("the group is gone"));
    }

    [Fact]
    public async Task RemovingAnInstalledListKeepsItInTheLibrary()
    {
        var f = await Open();
        var group = await InstallGroup(f, finished: true);

        var removal = f.Source.InstalledRemoval(CollectionGroupId.From(group));

        removal.Header.Should().Be("Remove modlist");
        removal.Icon.Should().Be(IconValues.PlaylistRemove);
        removal.Styling.Should().Be(ContextMenuItemStyling.Default);
        (await removal.CanExecute.FirstAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task RemovingAPendingListRemovesItFromTheLibrary()
    {
        var f = await Open();

        var removal = f.Source.PendingRemoval!;

        removal.Header.Should().Be("Remove modlist from library");
        removal.Icon.Should().Be(IconValues.DeleteOutline);
        removal.Styling.Should().Be(ContextMenuItemStyling.Critical);
        (await removal.CanExecute.FirstAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task ApplyingRemovalOfAPendingListDeletesTheFileFromTheLibrary()
    {
        var f = await Open();
        var controller = Substitute.For<IWorkspaceController>();

        await f.Source.PendingRemoval!.Remove(controller);

        WabbajackModlistLibraryFile.Load(Connection.Db, f.File.Id).IsValid().Should().BeFalse();
    }

    [Fact]
    public async Task ConfirmingTheRemovalOfAPendingListAsksTheListsPlanDialogNotTheNameDialog()
    {
        var f = await Open();
        var windows = Substitute.For<IWindowManager>();
        Dialog? shown = null;
        windows.ShowDialog(Arg.Do<IDialog>(d => shown = (Dialog)d), Arg.Any<DialogWindowType>())
            .Returns(new StandardDialogResult { ButtonId = ButtonDefinitionId.Accept });

        await f.Source.PendingRemoval!.ConfirmAndRemove(windows, Substitute.For<IWorkspaceController>());

        shown!.ViewModel.WindowTitle.Should().Be($"Remove '{f.File.Name}' from the library?");
        WabbajackModlistLibraryFile.Load(Connection.Db, f.File.Id).IsValid().Should().BeFalse();
    }

    [Fact]
    public async Task CancellingTheRemovalOfAPendingListKeepsIt()
    {
        var f = await Open();
        var windows = Substitute.For<IWindowManager>();
        windows.ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>())
            .Returns(new StandardDialogResult { ButtonId = ButtonDefinitionId.Cancel });

        await f.Source.PendingRemoval!.ConfirmAndRemove(windows, Substitute.For<IWorkspaceController>());

        WabbajackModlistLibraryFile.Load(Connection.Db, f.File.Id).IsValid().Should().BeTrue();
    }

    [Fact]
    public async Task AnUnpinnedSourceCannotBeSubscribedBeforeItsDownloadsExist()
    {
        var f = await Open(pinned: false);

        var act = () => f.Source.Group.FirstAsync().ToTask();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
