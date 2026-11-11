using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Wabbajack.Models;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Networking.ModUpdates.Mixins;
using SilverModManager.Sdk.Games;
using SilverModManager.App.UI.Overlays;
using SilverModManager.App.UI.Overlays.Generic.MessageBox.Ok;
using SilverModManager.App.UI.Pages.Diagnostics;
using SilverModManager.App.UI.Windows;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.ModUpdates;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>The two actions a regulation merge offers when it has no vanilla file for the build some mods were made for.</summary>
public class VanillaMissingActionTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly NexusModsGameId EldenRing = NexusModsGameId.From(4333);

    private async Task<LoadoutItemGroup.ReadOnly> AddMod(Loadout.ReadOnly loadout, string name, bool disabled = false)
    {
        using var tx = Connection.BeginTransaction();
        var group = new LoadoutItemGroup.New(tx, out var groupId)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, groupId) { Name = name, LoadoutId = loadout },
        };
        if (disabled) tx.Add(groupId, LoadoutItem.Disabled, Null.Instance);
        var result = await tx.Commit();
        return result.Remap(group);
    }

    private ICollectionOwnership Ownership => Provider.GetRequiredService<ICollectionOwnership>();

    private static LoadoutItemId IdOf(LoadoutItemGroup.ReadOnly mod) => mod.AsLoadoutItem().LoadoutItemId;

    private bool IsDisabled(LoadoutItemGroup.ReadOnly mod) => LoadoutItem.Load(Connection.Db, IdOf(mod)).IsDisabled;

    /// <summary>A mod installed from a Nexus file; returns the file's metadata and a newer file of the same page.</summary>
    private async Task<(LoadoutItemGroup.ReadOnly Mod, NexusModsFileMetadata.ReadOnly Installed, NexusModsFileMetadata.ReadOnly Newer)> AddNexusMod(Loadout.ReadOnly loadout, string name, uint modId, EntityId? parent = null)
    {
        using var tx = Connection.BeginTransaction();
        var item = default(LibraryItem.New);
        var file = new LibraryFile.New(tx, out var libraryId)
        {
            FileName = name + ".zip",
            Hash = NexusMods.Hashing.xxHash3.Hash.From((ulong)modId),
            Size = NexusMods.Paths.Size.From(1),
            LibraryItem = item = new LibraryItem.New(tx, libraryId) { Name = name },
        };
        var page = new NexusModsModPageMetadata.New(tx)
        {
            Uid = new ModUid(ModId.From(modId), EldenRing),
            Name = name,
            GameDomain = GameDomain.From("eldenring"),
            UpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
            DataUpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
        };
        var installed = new NexusModsFileMetadata.New(tx)
        {
            Uid = new FileUid(FileId.From(modId * 10), EldenRing),
            Name = name,
            Version = "1.0",
            UploadedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
            ModPageId = page,
        };
        var newer = new NexusModsFileMetadata.New(tx)
        {
            Uid = new FileUid(FileId.From(modId * 10 + 1), EldenRing),
            Name = name,
            Version = "2.0",
            UploadedAt = DateTimeOffset.UnixEpoch.UtcDateTime.AddDays(1),
            ModPageId = page,
        };
        tx.Add(libraryId, NexusModsLibraryItem.FileMetadataId, installed.Id);
        tx.Add(libraryId, NexusModsLibraryItem.ModPageMetadataId, page.Id);

        var group = new LoadoutItemGroup.New(tx, out var groupId)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, groupId) { Name = name, LoadoutId = loadout },
        };
        if (parent is { } parentId) tx.Add(groupId, LoadoutItem.ParentId, parentId);
        _ = new LibraryLinkedLoadoutItem.New(tx, groupId) { LibraryItemId = item, LoadoutItemGroup = group };
        var result = await tx.Commit();
        return (result.Remap(group), result.Remap(installed), result.Remap(newer));
    }

    private static IModUpdateService UpdatesWith(Func<NexusModsModPageMetadata.ReadOnly, Optional<ModUpdatesOnModPage>> forPage, CacheUpdaterResultStatus status = CacheUpdaterResultStatus.Ok)
    {
        var service = Substitute.For<IModUpdateService>();
        service.CheckAndUpdateModPages(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(Task.FromResult(PerFeedCacheUpdaterResult<PageMetadataMixin>.WithStatus(status)));
        service.HasModPageUpdatesAvailable(Arg.Any<NexusModsModPageMetadata.ReadOnly>(), Arg.Any<Func<ModUpdatesOnModPage, ModUpdatesOnModPage?>?>())
            .Returns(call => forPage(call.Arg<NexusModsModPageMetadata.ReadOnly>()));
        return service;
    }

    private sealed class Services(IServiceProvider inner, IOverlayController overlays) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IOverlayController) ? overlays : inner.GetService(serviceType);
    }

    private (IServiceProvider Services, List<MessageBoxOkViewModel> Shown) CapturingOverlays()
    {
        var shown = new List<MessageBoxOkViewModel>();
        var overlays = Substitute.For<IOverlayController>();
        overlays.EnqueueAndWait(Arg.Any<IOverlayViewModel<R3.Unit>>()).Returns(call =>
        {
            shown.Add((MessageBoxOkViewModel)call.Arg<IOverlayViewModel<R3.Unit>>());
            return Task.FromResult(R3.Unit.Default);
        });
        return (new Services(Provider, overlays), shown);
    }

    private static IWindowManager Answering(ButtonDefinitionId button)
    {
        var windows = Substitute.For<IWindowManager>();
        windows.ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>()).Returns(new StandardDialogResult { ButtonId = button });
        return windows;
    }

    [Fact]
    public void Test_BothActionsHaveAHandlerInTheApp()
    {
        Provider.GetServices<IDiagnosticActionHandler>().Select(h => h.ActionId)
            .Should().Contain([VanillaMissingActions.CheckModUpdatesId, VanillaMissingActions.DisableModsId]);
    }

    #region report text

    private static ModUpdateVerdict Verdict(ModUpdateOutcome outcome) => new(outcome);

    [Fact]
    public void Test_TheReportSaysWhichModsHaveNewerFilesWhichDoNotAndWhichHaveNoNexusPage()
    {
        var text = ModUpdateReport.Describe(
            [("A", Verdict(ModUpdateOutcome.NewerFiles)), ("B", Verdict(ModUpdateOutcome.NewerFiles)), ("C", Verdict(ModUpdateOutcome.UpToDate)), ("D", Verdict(ModUpdateOutcome.NoNexusPage))],
            CacheUpdaterResultStatus.Ok);

        text.Should().Contain("Newer files exist for A, B; none for C; no Nexus page known for D.");
        text.Should().Contain("can't tell which game build", "a newer file is not necessarily built for the installed game");
        text.Should().NotContain("already knew");
    }

    [Fact]
    public void Test_TheReportLeavesOutTheClausesNoModFitsAndSaysSoWhenNothingWasAsked()
    {
        ModUpdateReport.Describe([("A", Verdict(ModUpdateOutcome.UpToDate))], CacheUpdaterResultStatus.Ok)
            .Should().Contain("None of these mods has a newer file").And.NotContain("Newer files exist");

        ModUpdateReport.Describe([("A", Verdict(ModUpdateOutcome.NewerFiles))], CacheUpdaterResultStatus.NotLoggedIn)
            .Should().Contain("Log in to Nexus Mods").And.Contain("already knew");
    }

    #endregion

    #region outcome

    [Fact]
    public async Task Test_AModNotInstalledFromNexusHasNoPageToAskAbout()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Updates");
        var mod = await AddMod(loadout, "Local mod");

        ModUpdateReport.Outcome(Connection.Db, Ownership, UpdatesWith(_ => Optional<ModUpdatesOnModPage>.None), IdOf(mod)).Outcome.Should().Be(ModUpdateOutcome.NoNexusPage);
    }

    [Fact]
    public async Task Test_ANexusModIsNewerOnlyWhenTheCacheMapsItsOwnFileToANewerOne()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Updates");
        var (mod, installed, newer) = await AddNexusMod(loadout, "Has update", 501);
        var (other, otherFile, _) = await AddNexusMod(loadout, "Other file updates", 502);

        var forThisFile = UpdatesWith(_ => Optional.Some(new ModUpdatesOnModPage([new ModUpdateOnPage(installed, [newer])])));
        var forAnotherFile = UpdatesWith(_ => Optional.Some(new ModUpdatesOnModPage([new ModUpdateOnPage(otherFile, [newer])])));

        ModUpdateReport.Outcome(Connection.Db, Ownership, forThisFile, IdOf(mod)).Outcome.Should().Be(ModUpdateOutcome.NewerFiles);
        ModUpdateReport.Outcome(Connection.Db, Ownership, forAnotherFile, IdOf(mod)).Outcome.Should().Be(ModUpdateOutcome.UpToDate, "the page has an update, but for a different file than the one installed");
        ModUpdateReport.Outcome(Connection.Db, Ownership, UpdatesWith(_ => Optional<ModUpdatesOnModPage>.None), IdOf(other)).Outcome.Should().Be(ModUpdateOutcome.UpToDate);
    }

    private async Task<EntityId> AddCollection(Loadout.ReadOnly loadout, string name)
    {
        using var tx = Connection.BeginTransaction();
        var group = new CollectionGroup.New(tx, out var id)
        {
            IsReadOnly = true,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id) { Name = name, LoadoutId = loadout },
            },
        };
        return (await tx.Commit()).Remap(group).Id;
    }

    [Fact]
    public async Task Test_AModOfAModlistSaysToUpdateTheList()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Updates");
        var list = await WabbajackGroupSeed.AddList(Connection, "1", name: "Yggdrasil");
        var listGroup = await WabbajackGroupSeed.AddGroup(Connection, list.Id, loadout.LoadoutId, "Yggdrasil");
        var (mod, installed, newer) = await AddNexusMod(loadout, "SkyUI", 701, listGroup);
        var updates = UpdatesWith(_ => Optional.Some(new ModUpdatesOnModPage([new ModUpdateOnPage(installed, [newer])])));

        var verdict = ModUpdateReport.Outcome(Connection.Db, Ownership, updates, IdOf(mod));

        verdict.Outcome.Should().Be(ModUpdateOutcome.PartOfLockedCollection, "the page has a newer file, but the list decides what the mod is");
        ModUpdateReport.Describe([("SkyUI", verdict)], CacheUpdaterResultStatus.Ok)
            .Should().Contain("SkyUI is part of Yggdrasil; Yggdrasil is a Wabbajack modlist; it decides what it contains, so update it there")
            .And.NotContain("Nexus page known");
    }

    [Fact]
    public async Task Test_AModOfANexusCollectionSaysSo()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Updates");
        var collection = await AddCollection(loadout, "Elden Overhaul");
        var (mod, installed, newer) = await AddNexusMod(loadout, "Great Runes", 702, collection);
        var updates = UpdatesWith(_ => Optional.Some(new ModUpdatesOnModPage([new ModUpdateOnPage(installed, [newer])])));

        var verdict = ModUpdateReport.Outcome(Connection.Db, Ownership, updates, IdOf(mod));

        verdict.Outcome.Should().Be(ModUpdateOutcome.PartOfLockedCollection);
        ModUpdateReport.Describe([("Great Runes", verdict)], CacheUpdaterResultStatus.Ok)
            .Should().Contain("Great Runes is part of Elden Overhaul; Elden Overhaul is a Nexus collection; it decides what it contains, so update it there");
    }

    #endregion

    #region check for updates

    [Fact]
    public async Task Test_CheckingAsksNexusAgainAndReportsEachModInTheDialog()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Updates");
        var (withUpdate, installed, newer) = await AddNexusMod(loadout, "Great Rune Overhaul", 601);
        var (current, _, _) = await AddNexusMod(loadout, "Better Bows", 602);
        var local = await AddMod(loadout, "moveset_modpack");
        var updates = UpdatesWith(page => page.Uid.ModId == ModId.From(601)
            ? Optional.Some(new ModUpdatesOnModPage([new ModUpdateOnPage(installed, [newer])]))
            : Optional<ModUpdatesOnModPage>.None);
        var (services, shown) = CapturingOverlays();
        var handler = new CheckModUpdatesActionHandler(services, updates);

        handler.ActionId.Should().Be(VanillaMissingActions.CheckModUpdatesId);
        await handler.ExecuteAsync(loadout.LoadoutId, [..VanillaMissingActions.CheckModUpdates([IdOf(withUpdate), IdOf(current), IdOf(local)]).Arguments]).WaitAsync(TimeSpan.FromSeconds(30));

        await updates.Received(1).CheckAndUpdateModPages(Arg.Any<CancellationToken>(), Arg.Any<bool>(), false);
        var dialog = shown.Should().ContainSingle().Subject;
        dialog.Description.Should().Contain("Newer files exist for Great Rune Overhaul; none for Better Bows; no Nexus page known for moveset_modpack.");
    }

    [Fact]
    public async Task Test_CheckingRefusesAnArgumentThatIsNotAModBeforeAskingNexus()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Updates");
        var updates = UpdatesWith(_ => Optional<ModUpdatesOnModPage>.None);
        var (services, shown) = CapturingOverlays();
        var handler = new CheckModUpdatesActionHandler(services, updates);

        var act = () => handler.ExecuteAsync(loadout.LoadoutId, ["not an id"]);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*not an id*");
        await updates.DidNotReceive().CheckAndUpdateModPages(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<bool>());
        shown.Should().BeEmpty();
    }

    #endregion

    #region disable

    private DisableModsActionHandler DisableHandler(IWindowManager windows, IDiagnosticManager manager) => new(Provider, windows, manager);

    [Fact]
    public async Task Test_DisablingAsksFirstThenDisablesOnlyTheNamedModsAndRefreshes()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Disable");
        var a = await AddMod(loadout, "Alpha");
        var b = await AddMod(loadout, "Beta");
        var untouched = await AddMod(loadout, "Gamma");
        var windows = Answering(ButtonDefinitionId.Accept);
        var manager = Substitute.For<IDiagnosticManager>();
        var handler = DisableHandler(windows, manager);

        handler.ActionId.Should().Be(VanillaMissingActions.DisableModsId);
        await handler.ExecuteAsync(loadout.LoadoutId, [..VanillaMissingActions.DisableMods("1", [IdOf(a), IdOf(b)], onlyBuild: true).Arguments]).WaitAsync(TimeSpan.FromSeconds(30));

        await windows.Received(1).ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>());
        IsDisabled(a).Should().BeTrue();
        IsDisabled(b).Should().BeTrue();
        IsDisabled(untouched).Should().BeFalse();
        await manager.Received(1).RefreshAsync(loadout.LoadoutId);
    }

    [Fact]
    public async Task Test_DecliningDisablesNothingAndDoesNotRefresh()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Disable");
        var a = await AddMod(loadout, "Alpha");
        var manager = Substitute.For<IDiagnosticManager>();
        var handler = DisableHandler(Answering(ButtonDefinitionId.Cancel), manager);

        await handler.ExecuteAsync(loadout.LoadoutId, [..VanillaMissingActions.DisableMods("1", [IdOf(a)], onlyBuild: true).Arguments]).WaitAsync(TimeSpan.FromSeconds(30));

        IsDisabled(a).Should().BeFalse();
        await manager.DidNotReceive().RefreshAsync(Arg.Any<LoadoutId>());
    }

    [Fact]
    public async Task Test_ModsAlreadyDisabledAreNotAskedAboutAndAnEmptyListAsksNothing()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Disable");
        var off = await AddMod(loadout, "Already off", disabled: true);
        var windows = Answering(ButtonDefinitionId.Accept);
        var manager = Substitute.For<IDiagnosticManager>();
        var handler = DisableHandler(windows, manager);

        await handler.ExecuteAsync(loadout.LoadoutId, [..VanillaMissingActions.DisableMods("1", [IdOf(off)], onlyBuild: true).Arguments]).WaitAsync(TimeSpan.FromSeconds(30));

        await windows.DidNotReceive().ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>());
        IsDisabled(off).Should().BeTrue();
    }

    [Fact]
    public async Task Test_DisablingRefusesAnArgumentThatIsNotAModWithoutAsking()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Disable");
        var windows = Answering(ButtonDefinitionId.Accept);
        var handler = DisableHandler(windows, Substitute.For<IDiagnosticManager>());

        var act = () => handler.ExecuteAsync(loadout.LoadoutId, ["not an id"]);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*not an id*");
        await windows.DidNotReceive().ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>());
    }

    [Fact]
    public void Test_TheConfirmationListsTheModsAndNamesTheOnesThatStayOn()
    {
        var text = DisableModsActionHandler.ConfirmationText(["Alpha", "Beta"], ["Required by a collection"]);

        text.Should().Contain("- Alpha").And.Contain("- Beta");
        text.Should().Contain("Required by a collection").And.Contain("stay");
        DisableModsActionHandler.ConfirmationText(["Alpha"], []).Should().NotContain("stay");
    }

    #endregion
}
