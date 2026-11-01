using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.GC;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Library;
using SilverModManager.Networking.Thunderstore;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Thunderstore;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// The library page applies every update the same way whichever source made it: the sources do the work and the page
/// shows the confirmation, the retry, the failed-install and the premium dialogs for what they report.
/// </summary>
public class LibraryApplyUpdatesTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly ButtonDefinitionId UpdateButton = ButtonDefinitionId.From("Update");
    private static readonly ButtonDefinitionId DeleteButton = ButtonDefinitionId.From("Delete");
    private static readonly ButtonDefinitionId KeepButton = ButtonDefinitionId.From("Keep");

    private sealed class Harness
    {
        public LibraryViewModel Vm { get; set; } = null!;
        public required ILibraryService Library { get; init; }
        public required IWindowNotificationService Toasts { get; init; }
        public required IErrorPresenter Errors { get; init; }
        public required Loadout.ReadOnly Loadout { get; init; }
        public List<string> DialogTitles { get; } = [];
        public Dictionary<string, ButtonDefinitionId> Answers { get; } = [];
        public string[] TitlesOf(params string[] titles) => DialogTitles.Where(titles.Contains).ToArray();
    }

    private IDisposable? _activation;

    public override async ValueTask DisposeAsync()
    {
        _activation?.Dispose();
        await base.DisposeAsync();
    }

    private async Task<Harness> CreateHarness(params IModSource[] sources)
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Apply updates " + Guid.NewGuid());
        var library = Substitute.For<ILibraryService>();
        library.CollectionsWithLibraryItems(Arg.Any<IEnumerable<LibraryItem.ReadOnly>>(), Arg.Any<bool>())
            .Returns(new Dictionary<CollectionGroup.ReadOnly, IReadOnlyList<(LibraryItem.ReadOnly libraryItem, LibraryLinkedLoadoutItem.ReadOnly linkedItem)>>());
        library.CollectionsWithLibraryItem(Arg.Any<LibraryItem.ReadOnly>(), Arg.Any<bool>())
            .Returns(Array.Empty<(CollectionGroup.ReadOnly collection, LibraryLinkedLoadoutItem.ReadOnly linkedItem)>());
        var toasts = Substitute.For<IWindowNotificationService>();
        var windows = Substitute.For<IWindowManager>();
        var errors = Substitute.For<IErrorPresenter>();
        var harness = new Harness
        {
            Library = library,
            Toasts = toasts,
            Errors = errors,
            Loadout = loadout,
        };
        windows.ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>()).Returns(call =>
        {
            var title = ((Dialog)call.Arg<IDialog>()).ViewModel.WindowTitle;
            lock (harness.DialogTitles) harness.DialogTitles.Add(title);
            return Task.FromResult(new StandardDialogResult { ButtonId = harness.Answers.GetValueOrDefault(title, ButtonDefinitionId.Cancel) });
        });
        harness.Vm = await OnUi(() => Task.FromResult(new LibraryViewModel(
            windows,
            new ServicesWithSources(Provider, sources).With(library).With(toasts).With(errors),
            Provider.GetRequiredService<IGameDomainToGameIdMappingCache>(),
            loadout.LoadoutId)));
        _activation = await OnUi(() => Task.FromResult(harness.Vm.Activator.Activate()));
        return harness;
    }

    private static CompositeItemModel<EntityId> RowWithIds(ulong id)
    {
        var row = new CompositeItemModel<EntityId>(EntityId.From(id));
        row.Add(LibraryColumns.Actions.LibraryItemIdsComponentKey, new LibraryComponents.LibraryItemIds(LibraryItemId.From(EntityId.From(id))));
        return row;
    }

    private async Task<LibraryItem.ReadOnly[]> SeedLibraryItems(params string[] names)
    {
        using var tx = Connection.BeginTransaction();
        var added = names.Select(name =>
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(name + Guid.NewGuid());
            var file = new LibraryFile.New(tx, out var id)
            {
                FileName = name,
                Hash = bytes.xxHash3(),
                Size = Size.From((ulong)bytes.Length),
                LibraryItem = new LibraryItem.New(tx, id) { Name = name },
            };
            return file.Id;
        }).ToArray();
        var result = await tx.Commit();
        return added.Select(id => LibraryItem.Load(result.Db, result[id])).ToArray();
    }

    private async Task<CollectionGroup.ReadOnly[]> SeedCollections(Loadout.ReadOnly loadout, bool readOnly, params string[] names)
    {
        using var tx = Connection.BeginTransaction();
        var added = names.Select(name =>
        {
            _ = new CollectionGroup.New(tx, out var id)
            {
                IsReadOnly = readOnly,
                LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
                {
                    IsGroup = true,
                    LoadoutItem = new LoadoutItem.New(tx, id) { Name = name, LoadoutId = loadout.LoadoutId },
                },
            };
            return id;
        }).ToArray();
        var result = await tx.Commit();
        return added.Select(id => CollectionGroup.Load(result.Db, result[id])).ToArray();
    }

    private static IReadOnlyDictionary<CollectionGroup.ReadOnly, IReadOnlyList<(LibraryItem.ReadOnly libraryItem, LibraryLinkedLoadoutItem.ReadOnly linkedItem)>> Holding(params CollectionGroup.ReadOnly[] collections)
        => collections.ToDictionary(collection => collection, _ => (IReadOnlyList<(LibraryItem.ReadOnly, LibraryLinkedLoadoutItem.ReadOnly)>)[]);

    private static void Send(Harness harness, ModUpdate update, bool replace)
    {
        var row = RowWithIds(0xB00001);
        if (replace) harness.Vm.Adapter.MessageSubject.OnNext(new UpdateAndReplaceMessage(update, row));
        else harness.Vm.Adapter.MessageSubject.OnNext(new UpdateAndKeepOldMessage(update, row));
    }

    [Fact]
    public async Task AReplaceAcrossTwoSources_AsksAboutSeveralCollectionsOnce_ThenAppliesEveryBatch()
    {
        var a = new FakeModSource("a");
        var b = new FakeModSource("b");
        var harness = await CreateHarness(a, b);
        var items = await SeedLibraryItems("first", "second");
        var collections = await SeedCollections(harness.Loadout, readOnly: false, "Collection one", "Collection two");
        harness.Library.CollectionsWithLibraryItems(Arg.Any<IEnumerable<LibraryItem.ReadOnly>>(), true).Returns(Holding(collections));
        harness.Answers[Language.Library_Update_InstalledInMultipleCollections_Title] = UpdateButton;
        var updates = new ModUpdate[] { new FakeModUpdate([items[0].LibraryItemId], Source: "a"), new FakeModUpdate([items[1].LibraryItemId], Source: "b") };
        harness.Vm.Adapter.SelectedModels.Clear();
        foreach (var update in updates)
        {
            var row = RowWithIds(0xB10000 + (ulong)harness.Vm.Adapter.SelectedModels.Count);
            row.Add(LibraryColumns.Actions.UpdateComponentKey, new LibraryComponents.UpdateAction(update, R3.Observable.Return(update)));
            await OnUi(() => { harness.Vm.Adapter.SelectedModels.Add(row); return Task.CompletedTask; });
        }
        await Eventually(() => harness.Vm.UpdatableSelectionCount.Should().Be(2));

        harness.Vm.UpdateSelectedItemsCommand.Execute(R3.Unit.Default);

        await Eventually(() =>
        {
            a.Requests.Should().ContainSingle().Which.Should().Match<ModUpdateRequest>(r => r.Replace && r.MayOpenBrowser && r.Updates.SequenceEqual(new[] { updates[0] }));
            b.Requests.Should().ContainSingle().Which.Updates.Should().Equal(updates[1]);
        });
        harness.TitlesOf(Language.Library_Update_InstalledInMultipleCollections_Title).Should().HaveCount(1);
    }

    [Fact]
    public async Task DecliningTheSeveralCollectionsQuestion_AppliesNothing()
    {
        var a = new FakeModSource("a");
        var harness = await CreateHarness(a);
        var items = await SeedLibraryItems("held");
        var collections = await SeedCollections(harness.Loadout, readOnly: false, "Collection one", "Collection two");
        harness.Library.CollectionsWithLibraryItems(Arg.Any<IEnumerable<LibraryItem.ReadOnly>>(), true).Returns(Holding(collections));

        Send(harness, new FakeModUpdate([items[0].LibraryItemId], Source: "a"), replace: true);

        await Eventually(() => harness.TitlesOf(Language.Library_Update_InstalledInMultipleCollections_Title).Should().HaveCount(1));
        await Task.Delay(200);
        a.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ASourceThatReplacesNothing_NeverTriggersTheSeveralCollectionsQuestion_AndStillApplies()
    {
        var free = new FakeModSource("a") { Replaces = false };
        var harness = await CreateHarness(free);
        var items = await SeedLibraryItems("held");
        var collections = await SeedCollections(harness.Loadout, readOnly: false, "Collection one", "Collection two");
        harness.Library.CollectionsWithLibraryItems(Arg.Any<IEnumerable<LibraryItem.ReadOnly>>(), true).Returns(Holding(collections));

        Send(harness, new FakeModUpdate([items[0].LibraryItemId], Source: "a"), replace: true);

        await Eventually(() => free.Requests.Should().ContainSingle());
        harness.DialogTitles.Should().BeEmpty();
    }

    [Fact]
    public async Task TheSeveralCollectionsQuestion_OnlyCoversTheUpdatesThatWillBeReplaced()
    {
        var replacing = new FakeModSource("a");
        var free = new FakeModSource("b") { Replaces = false };
        var harness = await CreateHarness(replacing, free);
        var items = await SeedLibraryItems("replaced", "linked only");
        var collections = await SeedCollections(harness.Loadout, readOnly: false, "Collection one", "Collection two");
        harness.Library.CollectionsWithLibraryItems(
                Arg.Is<IEnumerable<LibraryItem.ReadOnly>>(asked => asked.Select(item => item.Id).SequenceEqual(new[] { items[0].Id })), true)
            .Returns(Holding(collections));
        harness.Answers[Language.Library_Update_InstalledInMultipleCollections_Title] = UpdateButton;
        var updates = new ModUpdate[] { new FakeModUpdate([items[0].LibraryItemId], Source: "a"), new FakeModUpdate([items[1].LibraryItemId], Source: "b") };
        harness.Vm.Adapter.SelectedModels.Clear();
        foreach (var update in updates)
        {
            var row = RowWithIds(0xB20000 + (ulong)harness.Vm.Adapter.SelectedModels.Count);
            row.Add(LibraryColumns.Actions.UpdateComponentKey, new LibraryComponents.UpdateAction(update, R3.Observable.Return(update)));
            await OnUi(() => { harness.Vm.Adapter.SelectedModels.Add(row); return Task.CompletedTask; });
        }
        await Eventually(() => harness.Vm.UpdatableSelectionCount.Should().Be(2));

        harness.Vm.UpdateSelectedItemsCommand.Execute(R3.Unit.Default);

        await Eventually(() =>
        {
            replacing.Requests.Should().ContainSingle();
            free.Requests.Should().ContainSingle();
        });
        harness.TitlesOf(Language.Library_Update_InstalledInMultipleCollections_Title).Should().HaveCount(1);
    }

    [Fact]
    public async Task KeepingTheOldFiles_NeverAsksAboutCollections()
    {
        var a = new FakeModSource("a");
        var harness = await CreateHarness(a);
        var items = await SeedLibraryItems("held");
        var collections = await SeedCollections(harness.Loadout, readOnly: false, "Collection one", "Collection two");
        harness.Library.CollectionsWithLibraryItems(Arg.Any<IEnumerable<LibraryItem.ReadOnly>>(), true).Returns(Holding(collections));

        Send(harness, new FakeModUpdate([items[0].LibraryItemId], Source: "a"), replace: false);

        await Eventually(() => a.Requests.Should().ContainSingle().Which.Replace.Should().BeFalse());
        harness.DialogTitles.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedDownload_OffersARetry_ThatAppliesOnlyTheNarrowedUpdate()
    {
        var a = new FakeModSource("a");
        var harness = await CreateHarness(a);
        var whole = new FakeModUpdate(2, Source: "a");
        var narrowed = new FakeModUpdate(1, NewestVersion: "3.0", Source: "a");
        a.Outcome = request => request.Updates.Contains(whole)
            ? ModUpdateOutcome.Nothing with { DownloadFailures = [new ModUpdateFailure(narrowed, "mod-3.0.zip", new HttpRequestException("offline"))] }
            : ModUpdateOutcome.Nothing;
        harness.Answers[Language.Library_Update_SomeDownloadsFailed_Title] = ButtonDefinitionId.Accept;

        Send(harness, whole, replace: true);

        await Eventually(() =>
        {
            a.Requests.Should().HaveCount(2);
            a.Requests[1].Updates.Should().Equal(narrowed);
            a.Requests[1].Replace.Should().BeTrue();
            a.Requests[1].MayOpenBrowser.Should().BeTrue();
        });
        harness.TitlesOf(Language.Library_Update_SomeDownloadsFailed_Title).Should().HaveCount(1);
    }

    [Fact]
    public async Task ARefusal_IsShownWithItsOwnText_AndOffersNoRetry()
    {
        var a = new FakeModSource("a");
        var harness = await CreateHarness(a);
        var update = new FakeModUpdate(1, Source: "a");
        var refusal = new ThunderstorePackageInDisabledGroupException("BepInExPack", "Off");
        a.Outcome = _ => ModUpdateOutcome.Nothing with { DownloadFailures = [new ModUpdateFailure(update, "mod-3.0.zip", refusal)] };

        Send(harness, update, replace: true);

        await Eventually(() => harness.Errors.Received(1).Report(refusal, Language.Library_Update_Activity));
        harness.TitlesOf(Language.Library_Update_SomeDownloadsFailed_Title).Should().BeEmpty();
        a.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task ARefusalNextToADownloadFailure_StillOffersTheRetryForTheDownload()
    {
        var a = new FakeModSource("a");
        var harness = await CreateHarness(a);
        var refused = new FakeModUpdate(1, Source: "a");
        var offline = new FakeModUpdate(2, Source: "a");
        var refusal = new ThunderstorePackageInDisabledGroupException("BepInExPack", "Off");
        a.Outcome = request => request.Updates.Contains(refused)
            ? ModUpdateOutcome.Nothing with
            {
                DownloadFailures =
                [
                    new ModUpdateFailure(refused, "refused-3.0.zip", refusal),
                    new ModUpdateFailure(offline, "offline-3.0.zip", new HttpRequestException("offline")),
                ],
            }
            : ModUpdateOutcome.Nothing;
        harness.Answers[Language.Library_Update_SomeDownloadsFailed_Title] = ButtonDefinitionId.Accept;

        Send(harness, refused, replace: true);

        await Eventually(() =>
        {
            a.Requests.Should().HaveCount(2);
            a.Requests[1].Updates.Should().Equal(offline);
        });
        await harness.Errors.Received(1).Report(refusal, Language.Library_Update_Activity);
    }

    [Fact]
    public async Task AFailedDownload_ThatTheUserDoesNotRetry_IsNotAppliedAgain()
    {
        var a = new FakeModSource("a");
        var harness = await CreateHarness(a);
        var update = new FakeModUpdate(1, Source: "a");
        a.Outcome = _ => ModUpdateOutcome.Nothing with { DownloadFailures = [new ModUpdateFailure(update, "mod.zip", new HttpRequestException("offline"))] };

        Send(harness, update, replace: true);

        await Eventually(() => harness.TitlesOf(Language.Library_Update_SomeDownloadsFailed_Title).Should().HaveCount(1));
        await Task.Delay(200);
        a.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task AFailedReplacement_ShowsTheInstallsFailedDialog_AndDeletingRemovesTheNewFile()
    {
        var a = new FakeModSource("a");
        var harness = await CreateHarness(a);
        var items = await SeedLibraryItems("old", "new");
        a.Outcome = _ => ModUpdateOutcome.Nothing with { Replacements = [new ModUpdateReplacement(items[0], items[1], LibraryItemReplacementResult.FailedUnknownReason)] };
        harness.Answers[Language.Library_Update_SomeInstallsFailed_Title] = DeleteButton;

        Send(harness, new FakeModUpdate(1, Source: "a"), replace: true);

        await Eventually(() => harness.Library.Received(1).RemoveLibraryItems(Arg.Is<IEnumerable<LibraryItem.ReadOnly>>(removed => removed.Select(item => item.Id).SequenceEqual(new[] { items[1].Id })), Arg.Any<GarbageCollectorRunMode>()));
        harness.TitlesOf(Language.Library_Update_SomeInstallsFailed_Title).Should().HaveCount(1);
        harness.Toasts.DidNotReceiveWithAnyArgs().ShowToast(default!, default, default, default, default);
    }

    [Fact]
    public async Task ASuccessfulReplacement_ShowsAToast_AndRemovesTheOldItem()
    {
        var a = new FakeModSource("a");
        var harness = await CreateHarness(a);
        var items = await SeedLibraryItems("old", "new");
        a.Outcome = _ => ModUpdateOutcome.Nothing with { Replacements = [new ModUpdateReplacement(items[0], items[1], LibraryItemReplacementResult.Success)] };

        Send(harness, new FakeModUpdate(1, Source: "a"), replace: true);

        await Eventually(() => harness.Library.Received(1).RemoveLibraryItems(Arg.Is<IEnumerable<LibraryItem.ReadOnly>>(removed => removed.Select(item => item.Id).SequenceEqual(new[] { items[0].Id })), Arg.Any<GarbageCollectorRunMode>()));
        harness.Toasts.Received(1).ShowToast(string.Format(Language.ToastNotification_Mod_updated____0_, "new"), ToastNotificationVariant.Success, Arg.Any<TimeSpan?>(), Arg.Any<DialogButtonDefinition[]?>(), Arg.Any<Action<ButtonDefinitionId>?>());
    }

    // The Thunderstore source is the real one; only the installer behind it is a substitute, so what is checked is
    // that its outcome goes through the same toast and removal as any other source's.
    [Fact]
    public async Task AThunderstoreReplacement_ShowsAToast_AndRemovesTheOldVersion()
    {
        var installer = Substitute.For<IThunderstoreInstaller>();
        var source = new ThunderstoreModSource(
            Substitute.For<IThunderstoreCatalogSource>(), new ThunderstoreUpdateChecker(Connection), Connection, installer, Substitute.For<IThunderstoreSource>(), []);
        var harness = await CreateHarness(source);
        var items = await SeedLibraryItems("Zehs-REPOLib-1.0.0.zip", "Zehs-REPOLib-1.1.0.zip");
        var package = new ThunderstorePackageRef("Zehs", "REPOLib");
        var oldVersion = new ThunderstoreVersionRef(package, new ThunderstoreVersionNumber(1, 0, 0));
        var newVersion = new ThunderstoreVersionRef(package, new ThunderstoreVersionNumber(1, 1, 0));
        installer.UpdateAsync(newVersion, Arg.Is<LibraryItem.ReadOnly>(item => item.Id == items[0].Id), Arg.Any<CancellationToken>())
            .Returns(ModUpdateOutcome.Nothing with { Replacements = [new ModUpdateReplacement(items[0], items[1], LibraryItemReplacementResult.Success)] });
        var update = ThunderstoreModUpdate.From([new ThunderstoreUpdate(items[0].Id, oldVersion, newVersion)])!;

        Send(harness, update, replace: true);

        await Eventually(() => harness.Library.Received(1).RemoveLibraryItems(Arg.Is<IEnumerable<LibraryItem.ReadOnly>>(removed => removed.Select(item => item.Id).SequenceEqual(new[] { items[0].Id })), Arg.Any<GarbageCollectorRunMode>()));
        harness.Toasts.Received(1).ShowToast(string.Format(Language.ToastNotification_Mod_updated____0_, "Zehs-REPOLib-1.1.0.zip"), ToastNotificationVariant.Success, Arg.Any<TimeSpan?>(), Arg.Any<DialogButtonDefinition[]?>(), Arg.Any<Action<ButtonDefinitionId>?>());
    }

    [Fact]
    public async Task AnOldItemHeldByAReadOnlyCollection_IsNotRemoved()
    {
        var a = new FakeModSource("a");
        var harness = await CreateHarness(a);
        var items = await SeedLibraryItems("old", "new");
        var readOnly = (await SeedCollections(harness.Loadout, readOnly: true, "Nexus collection")).Single();
        harness.Library.CollectionsWithLibraryItem(Arg.Is<LibraryItem.ReadOnly>(item => item.Id == items[0].Id), false)
            .Returns([(readOnly, default(LibraryLinkedLoadoutItem.ReadOnly))]);
        a.Outcome = _ => ModUpdateOutcome.Nothing with { Replacements = [new ModUpdateReplacement(items[0], items[1], LibraryItemReplacementResult.Success)] };

        Send(harness, new FakeModUpdate(1, Source: "a"), replace: true);

        await Eventually(() => harness.Toasts.ReceivedWithAnyArgs(1).ShowToast(default!, default, default, default, default));
        await Task.Delay(200);
        await harness.Library.DidNotReceiveWithAnyArgs().RemoveLibraryItems(default!, default);
    }

    [Fact]
    public async Task UpdateAll_WhenOneSourceCannotDownload_StillAppliesTheOther_ThenShowsThePremiumDialog()
    {
        var nexus = new FakeModSource("nexus");
        var other = new FakeModSource("other");
        nexus.Updates.Add(new FakeModUpdate(1, Source: "nexus"));
        other.Updates.Add(new FakeModUpdate(1, Source: "other"));
        nexus.Outcome = _ => ModUpdateOutcome.Nothing with { AccountCannotDownload = true };
        var harness = await CreateHarness(nexus, other);
        harness.Answers[Language.PremiumDialog_UpdateTitle] = ButtonDefinitionId.From("update-manually");

        harness.Vm.UpdateAllCommand.Execute(R3.Unit.Default);

        await Eventually(() =>
        {
            other.Requests.Should().ContainSingle().Which.Should().Match<ModUpdateRequest>(r => r.Replace && r.MayOpenBrowser == false);
            nexus.Requests.Should().ContainSingle().Which.MayOpenBrowser.Should().BeFalse();
            harness.TitlesOf(Language.PremiumDialog_UpdateTitle).Should().HaveCount(1);
        });
    }

    [Fact]
    public async Task UpdateAll_WithOnlyASourceThatCannotDownload_ShowsOnlyThePremiumDialog()
    {
        var free = new FakeModSource("a") { Replaces = false };
        free.Updates.Add(new FakeModUpdate(1, Source: "a"));
        free.Outcome = _ => ModUpdateOutcome.Nothing with { AccountCannotDownload = true };
        var harness = await CreateHarness(free);
        var items = await SeedLibraryItems("held");
        var collections = await SeedCollections(harness.Loadout, readOnly: false, "Collection one", "Collection two");
        harness.Library.CollectionsWithLibraryItems(Arg.Any<IEnumerable<LibraryItem.ReadOnly>>(), true).Returns(Holding(collections));
        free.Updates[0] = new FakeModUpdate([items[0].LibraryItemId], Source: "a");
        harness.Answers[Language.PremiumDialog_UpdateTitle] = ButtonDefinitionId.From("update-manually");

        harness.Vm.UpdateAllCommand.Execute(R3.Unit.Default);

        await Eventually(() => harness.TitlesOf(Language.PremiumDialog_UpdateTitle).Should().HaveCount(1));
        harness.DialogTitles.Should().Equal(Language.PremiumDialog_UpdateTitle);
    }

    [Fact]
    public async Task UpdateAll_WhenNoSourceHasUpdates_AppliesNothing_AndShowsNoDialog()
    {
        var a = new FakeModSource("a");
        var harness = await CreateHarness(a);

        harness.Vm.UpdateAllCommand.Execute(R3.Unit.Default);

        await Eventually(() => harness.Vm.IsUpdatingAll.Should().BeFalse());
        await Task.Delay(200);
        a.Requests.Should().BeEmpty();
        harness.DialogTitles.Should().BeEmpty();
    }
}
