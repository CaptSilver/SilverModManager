using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reactive.Linq;
using System.Text;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Platform.Storage;
using DynamicData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Sdk.Settings;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Standard;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Extensions;
using SilverModManager.App.UI.Overlays;
using SilverModManager.App.UI.Pages.Library;
using SilverModManager.App.UI.Pages.LibraryPage.Collections;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.Controls.MarkdownRenderer;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using DynamicData.Kernel;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Collections;
using SilverModManager.UI.Sdk.Icons;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Networking.NexusWebApi.UpdateFilters;
using NexusMods.Paths;
using SilverModManager.Abstractions.Downloads;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;
using ObservableCollections;
using OneOf;
using R3;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Models;
using SilverModManager.App.UI.Helpers;

namespace SilverModManager.App.UI.Pages.LibraryPage;

public partial class LibraryViewModel : APageViewModel<ILibraryViewModel>, ILibraryViewModel
{
    private readonly IConnection _connection;
    private readonly ILibraryService _libraryService;

    public string EmptyLibrarySubtitleText { get; }

    public ReactiveCommand<Unit> UpdateAllCommand { get; }
    /// <summary>
    /// Asks every mod source for updates. The parameter is <c>force</c>: the Refresh button passes true, entering
    /// the page false so a source with a fresh cache can skip the network.
    /// </summary>
    public ReactiveCommand<bool> RefreshUpdatesCommand { get; }
    public ReactiveCommand<Unit> InstallSelectedItemsCommand { get; }

    public ReactiveCommand<Unit> InstallSelectedItemsWithAdvancedInstallerCommand { get; }

    public ReactiveCommand<Unit> UpdateSelectedItemsCommand { get; }
    
    public ReactiveCommand<Unit> UpdateAndKeepOldSelectedItemsCommand { get; }

    public ReactiveCommand<Unit> RemoveSelectedItemsCommand { get; }
    
    public ReactiveCommand<Unit> DeselectItemsCommand { get; }

    public ReactiveCommand<Unit> OpenFilePickerCommand { get; }

    public ReactiveCommand<Unit> OpenNexusModsCommand { get; }
    public ReactiveCommand<Unit> OpenNexusModsCollectionsCommand { get; }

    [Reactive] public partial int SelectionCount { get; private set; }
    
    [Reactive] public partial int UpdatableSelectionCount { get; private set; }
    
    [Reactive] public partial bool HasAnyUpdatesAvailable { get; private set; }

    [Reactive] public partial bool IsUpdatingAll { get; private set; }

    [Reactive] public partial IStorageProvider? StorageProvider { get; set; }

    private readonly IServiceProvider _serviceProvider;
    private readonly ILibraryItemInstaller _advancedInstaller;
    private readonly IGameDomainToGameIdMappingCache _gameIdMappingCache;
    private readonly Loadout.ReadOnly _loadout;
    private readonly IWindowNotificationService _notificationService;
    private readonly ILoadoutManager _loadoutManager;

    public LibraryTreeDataGridAdapter Adapter { get; }
    private ReadOnlyObservableCollection<ILibraryCollectionCardViewModel> _collections = new([]);
    public ReadOnlyObservableCollection<ILibraryCollectionCardViewModel> Collections => _collections;

    private ReadOnlyObservableCollection<InstallationTarget> _installationTargets = new([]);
    public ReadOnlyObservableCollection<InstallationTarget> InstallationTargets => _installationTargets;

    [Reactive] public partial InstallationTarget? SelectedInstallationTarget { get; set; }

    public LibraryViewModel(
        IWindowManager windowManager,
        IServiceProvider serviceProvider,
        IGameDomainToGameIdMappingCache gameIdMappingCache,
        LoadoutId loadoutId) : base(windowManager)
    {
        _serviceProvider = serviceProvider;
        _gameIdMappingCache = gameIdMappingCache;
        _libraryService = serviceProvider.GetRequiredService<ILibraryService>();
        _connection = serviceProvider.GetRequiredService<IConnection>();
        _notificationService = serviceProvider.GetRequiredService<IWindowNotificationService>();
        _loadoutManager = serviceProvider.GetRequiredService<ILoadoutManager>();

        var collectionDownloader = new CollectionDownloader(serviceProvider);
        var tileImagePipeline = ImagePipelines.GetCollectionTileImagePipeline(serviceProvider);
        var userAvatarPipeline = ImagePipelines.GetUserAvatarPipeline(serviceProvider);

        var loadout = Loadout.Load(_connection.Db, loadoutId);
        var libraryFilter = new LibraryFilter(loadout, loadout.InstallationInstance.Game);
        
        Adapter = new LibraryTreeDataGridAdapter(serviceProvider, libraryFilter);

        _advancedInstaller = serviceProvider.GetRequiredKeyedService<ILibraryItemInstaller>("AdvancedManualInstaller_Direct");

        TabTitle = Language.LibraryPageTitle;
        TabIcon = IconValues.LibraryOutline;
        
        _loadout = Loadout.Load(_connection.Db, loadoutId.Value);
        var game = _loadout.InstallationInstance.Game;

        EmptyLibrarySubtitleText = string.Format(Language.FileOriginsPageViewModel_EmptyLibrarySubtitleText, game.DisplayName);

        DeselectItemsCommand = new ReactiveCommand<Unit>(_ =>
        {
            Adapter.ClearSelection();
        });

        RefreshUpdatesCommand = new ReactiveCommand<bool>(
            executeAsync: (force, token) => RefreshUpdates(force, token),
            awaitOperation: AwaitOperation.Switch
        );

        // Create an observable that determines when UpdateAll can execute
        var canUpdateAll = this.WhenAnyValue(vm => vm.IsUpdatingAll)
            .ToObservable()
            .Select(isUpdating => !isUpdating);

        UpdateAllCommand = canUpdateAll.ToReactiveCommand<Unit>(
            executeAsync: (_, cancellationToken) => UpdateAllItems(cancellationToken),
            awaitOperation: AwaitOperation.Sequential,
            initialCanExecute: true,
            configureAwait: false
        );

        var hasSelection = Adapter.SelectedModels
            .ObserveCountChanged()
            .Select(count => count > 0);
        
        InstallSelectedItemsCommand = hasSelection.ToReactiveCommand<Unit>(
            executeAsync: (_, cancellationToken) => InstallSelectedItems(useAdvancedInstaller: false, cancellationToken),
            awaitOperation: AwaitOperation.Parallel,
            initialCanExecute: false,
            configureAwait: false
        );

        InstallSelectedItemsWithAdvancedInstallerCommand = hasSelection.ToReactiveCommand<Unit>(
            executeAsync: (_, cancellationToken) => InstallSelectedItems(useAdvancedInstaller: true, cancellationToken),
            awaitOperation: AwaitOperation.Parallel,
            initialCanExecute: false,
            configureAwait: false
        );

        var hasUpdatableSelection = Adapter.SelectedModels
            .ObserveCountChanged()
            .Select(_ => GetSelectedModelsWithUpdates().Any());

        var canUpdateSelected = R3.Observable.CombineLatest(
            hasUpdatableSelection,
            this.WhenAnyValue(vm => vm.IsUpdatingAll).ToObservable(),
            (hasUpdates, isUpdatingAll) => hasUpdates && !isUpdatingAll
        );

        UpdateSelectedItemsCommand = canUpdateSelected.ToReactiveCommand<Unit>(
            executeAsync: (_, cancellationToken) => UpdateSelectedItems(cancellationToken),
            awaitOperation: AwaitOperation.Parallel,
            initialCanExecute: false,
            configureAwait: false
        );

        var canUpdateAndKeepOldSelected = R3.Observable.CombineLatest(
            hasUpdatableSelection,
            this.WhenAnyValue(vm => vm.IsUpdatingAll).ToObservable(),
            (hasUpdates, isUpdatingAll) => hasUpdates && !isUpdatingAll
        );

        UpdateAndKeepOldSelectedItemsCommand = canUpdateAndKeepOldSelected.ToReactiveCommand<Unit>(
            executeAsync: (_, cancellationToken) => UpdateAndKeepOldSelectedItems(cancellationToken),
            awaitOperation: AwaitOperation.Parallel,
            initialCanExecute: false,
            configureAwait: false
        );

        RemoveSelectedItemsCommand = hasSelection.ToReactiveCommand<Unit>(
            executeAsync: (_, cancellationToken) => RemoveSelectedItems(cancellationToken),
            awaitOperation: AwaitOperation.Parallel,
            initialCanExecute: false,
            configureAwait: false
        );

        var canUseFilePicker = this.WhenAnyValue(vm => vm.StorageProvider)
            .ToObservable()
            .WhereNotNull()
            .Select(x => x.CanOpen);

        OpenFilePickerCommand = canUseFilePicker.ToReactiveCommand<Unit>(
            executeAsync: (_, cancellationToken) => AddFilesFromDisk(RequireStorageProvider(StorageProvider), cancellationToken),
            awaitOperation: AwaitOperation.Parallel,
            initialCanExecute: true,
            configureAwait: false
        );

        var osInterop = serviceProvider.GetRequiredService<IOSInterop>();
        // Games without a Nexus id have no page or collections on Nexus Mods to open.
        var isOnNexusMods = R3.Observable.Return(game.IsOnNexusMods());

        OpenNexusModsCommand = isOnNexusMods.ToReactiveCommand<Unit>(execute: _ =>
        {
            var gameDomain = _gameIdMappingCache[game.NexusModsGameId.Value];
            var gameUri = NexusModsUrlBuilder.GetGameUri(gameDomain);
            osInterop.OpenUri(gameUri);
        });

        OpenNexusModsCollectionsCommand = isOnNexusMods.ToReactiveCommand<Unit>(execute: _ =>
        {
            var gameDomain = _gameIdMappingCache[game.NexusModsGameId.Value];
            var gameUri = NexusModsUrlBuilder.GetBrowseCollectionsUri(gameDomain);
            osInterop.OpenUri(gameUri);
        });

        this.WhenActivated(disposables =>
        {
            Disposable.Create(this, static vm => vm.StorageProvider = null).AddTo(disposables);
            Adapter.Activate().AddTo(disposables);

            Adapter.MessageSubject.SubscribeAwait(
                onNextAsync: async (message, cancellationToken) =>
                {
                    await message.Match(
                        async installMessage =>
                        {
                            foreach (var id in installMessage.Ids)
                            {
                                var libraryItem = LibraryItem.Load(_connection.Db, id);
                                if (!libraryItem.IsValid()) continue;
                                await InstallLibraryItem(libraryItem, _loadout, GetInstallationTarget(), cancellationToken);
                            }
                        },
                        async updateAndReplaceMessage => await HandleUpdateAndReplaceMessage(updateAndReplaceMessage, cancellationToken),
                        async updateAndKeepOldMessage => await HandleUpdateAndKeepOldMessage(updateAndKeepOldMessage, cancellationToken),
                        async viewChangelogMessage => await HandleViewChangelogMessage(viewChangelogMessage, cancellationToken),
                        async viewModPageMessage => await HandleViewModPageMessage(viewModPageMessage, cancellationToken),
                        async hideUpdatesMessage => await HandleHideUpdatesMessage(hideUpdatesMessage, cancellationToken),
                        async deleteItemMessage => await HandleDeleteItemMessage(deleteItemMessage, cancellationToken),
                        async destinationMessage => await HandleDestinationMessage(destinationMessage, cancellationToken)
                    );
                },
                awaitOperation: AwaitOperation.Parallel,
                configureAwait: false
            ).AddTo(disposables);

            LoadoutQueries2.MutableCollections(_connection, _loadout.LoadoutId)
                .Observe(r => r.GroupId)
                .FilterImmutable(r => IsInstallTarget(CollectionGroup.Load(_connection.Db, r.GroupId)))
                .Transform(r => new InstallationTarget(r.GroupId, r.Name))
                .SortBy(target => target.Id.Value.Value)
                .DistinctUntilChanged()
                .Bind(out _installationTargets)
                .Subscribe()
                .AddTo(disposables);

            var nexusCards = CollectionRevisionMetadata.ObserveAll(_connection)
                .FilterImmutable(revision => game.NexusModsGameId.IsNexusGame(revision.Collection.GameId))
                .OnUI()
                .Transform(ILibraryCollectionCardViewModel (revision) => new CollectionCardViewModel(
                    collectionDownloader: collectionDownloader,
                    tileImagePipeline: tileImagePipeline,
                    userAvatarPipeline: userAvatarPipeline,
                    windowManager: WindowManager,
                    workspaceId: WorkspaceId,
                    revision: revision,
                    targetLoadout: _loadout)
                );

            // A modlist is a collection too; the card says it is a Wabbajack modlist.
            // A list for a game the app has no title for is unassigned and shows under every game: hiding it would make
            // the file impossible to find and remove.
            var modlistCards = LibraryDestinations.ObserveShownFor(_connection, game.GameId)
                .Transform(datom => WabbajackModlistLibraryFile.Load(_connection.Db, datom.E))
                .FilterImmutable(static file => file.IsValid())
                .OnUI()
                .Transform(ILibraryCollectionCardViewModel (file) => new WabbajackModlistCardViewModel(
                    provider: _serviceProvider,
                    windowManager: WindowManager,
                    workspaceId: WorkspaceId,
                    file: file,
                    loadout: _loadout.LoadoutId)
                );

            nexusCards.Or(modlistCards)
                .Bind(out _collections)
                .Subscribe()
                .AddTo(disposables);

            // Update the selection count based on the selected models
            Adapter.SelectedModels
                .ObserveChanged()
                .ObserveOnUIThreadDispatcher()
                .Subscribe(_ => 
                {
                    SelectionCount = GetSelectedIds().Length;
                    UpdatableSelectionCount = GetSelectedModelsWithUpdates().Count();
                })
                .AddTo(disposables);

            // Update All is offered while any source has an update to apply.
            var sources = _serviceProvider.GetRequiredService<ModSourceResolver>().Sources;
            System.Reactive.Linq.Observable
                .CombineLatest(sources.Select(source => source.ObserveHasAnyUpdates(game)))
                .Select(static hasAny => hasAny.Any(static has => has))
                .OnUI()
                .Subscribe(hasUpdates => HasAnyUpdatesAvailable = hasUpdates)
                .AddTo(disposables);

            // Auto check updates on entering library.
            R3.Observable
                .Return(Unit.Default)
                .ObserveOnThreadPool()
                .Subscribe(this,static (_, self) => self.RefreshUpdatesCommand.Execute(false))
                .AddTo(disposables);
        });
    }

    private ValueTask HandleUpdateAndReplaceMessage(UpdateAndReplaceMessage message, CancellationToken cancellationToken)
        => ApplyUpdates([message.Update], replace: true, mayOpenBrowser: true, cancellationToken);

    private ValueTask HandleUpdateAndKeepOldMessage(UpdateAndKeepOldMessage message, CancellationToken cancellationToken)
        => ApplyUpdates([message.Update], replace: false, mayOpenBrowser: true, cancellationToken);

    /// <summary>
    /// The one way every Update button, Update Selected and Update All applies updates: each source applies its own
    /// batch, then the dialogs and toasts for what happened are shown here. <paramref name="mayOpenBrowser"/> is
    /// false for Update All, where opening a browser tab per mod is not an answer; a source that could only do
    /// that says so and the premium dialog is shown once at the end. <paramref name="confirmed"/> is set when the
    /// user has already agreed to replace in several collections, as on a retry.
    /// </summary>
    private async ValueTask ApplyUpdates(IReadOnlyList<ModUpdate> updates, bool replace, bool mayOpenBrowser, CancellationToken cancellationToken, bool confirmed = false)
    {
        if (updates.Count == 0) return;
        if (replace && confirmed is false && await ConfirmReplacingInSeveralCollections(updates) is false) return;

        var resolver = _serviceProvider.GetRequiredService<ModSourceResolver>();
        var outcomes = new List<ModUpdateOutcome>();
        foreach (var batch in updates.GroupBy(update => resolver.For(update) ?? throw new InvalidOperationException($"No mod source made the update to {update.NewestVersion}.")))
            outcomes.Add(await batch.Key.ApplyUpdates(new ModUpdateRequest(batch.ToArray(), replace, mayOpenBrowser), cancellationToken));

        var replacements = outcomes.SelectMany(outcome => outcome.Replacements).ToArray();
        foreach (var replacement in replacements.Where(replacement => replacement.Result == LibraryItemReplacementResult.Success))
            _notificationService.ShowToast(string.Format(Language.ToastNotification_Mod_updated____0_, replacement.New.Name), ToastNotificationVariant.Success);

        // Some (but not all) downloads failed, so show relevant dialog.
        var failures = outcomes.SelectMany(outcome => outcome.DownloadFailures).ToArray();
        if (failures.Length > 0)
            await ShowSomeDownloadsFailedDialog(failures, replace, mayOpenBrowser, cancellationToken);

        // Some installs failed, ask if they want to keep old files
        if (replacements.Any(replacement => replacement.Result != LibraryItemReplacementResult.Success))
            await ShowSomeInstallsFailedDialog(replacements);

        await RemoveOldLibraryItemsIfNotInReadOnlyCollections(replacements.Where(replacement => replacement.Result == LibraryItemReplacementResult.Success));

        if (outcomes.Any(outcome => outcome.AccountCannotDownload))
            await PremiumDialog.ShowUpdatePremiumDialog(WindowManager, _serviceProvider.GetRequiredService<IOSInterop>());
    }

    /// <summary>
    /// A replace changes every collection that holds the old files, so when that is more than one the user is asked.
    /// </summary>
    private async ValueTask<bool> ConfirmReplacingInSeveralCollections(IEnumerable<ModUpdate> updates)
    {
        var db = _connection.Db;
        var libraryItemsToUpdate = updates
            .SelectMany(update => update.CurrentItems)
            .Distinct()
            .Select(id => LibraryItem.Load(db, id))
            .Where(item => item.IsValid())
            .ToArray();

        var collectionsAffected = _libraryService.CollectionsWithLibraryItems(libraryItemsToUpdate, excludeReadOnlyCollections: true);
        if (collectionsAffected.Count < 2) return true;

        (ButtonDefinitionId updateButtonId, StandardDialogResult dialogResult) = await ShowInstalledInMultipleCollectionsDialog(collectionsAffected, collectionsAffected.Count);
        return dialogResult.ButtonId == updateButtonId;
    }

    private async Task ShowSomeDownloadsFailedDialog(
        IReadOnlyList<ModUpdateFailure> downloadFailures,
        bool replace,
        bool mayOpenBrowser,
        CancellationToken ct)
    {
        var modsWhichFailed = new StringBuilder();
        foreach (var failure in downloadFailures)
            modsWhichFailed.AppendLine(failure.Name);

        var cancelFailedDownloads = new DialogButtonDefinition(
            Text: Language.Library_Update_SomeDownloadsFailed_Option_Cancel,
            Id: ButtonDefinitionId.Cancel,
            ButtonAction: ButtonAction.Reject,
            ButtonStyling: ButtonStyling.None
        );
        var retryFailedDownloads = new DialogButtonDefinition(
            Text: Language.Library_Update_SomeDownloadsFailed_Option_Retry,
            Id: ButtonDefinitionId.Accept,
            ButtonAction: ButtonAction.Accept,
            ButtonStyling: ButtonStyling.Default
        );

        var allFailedDialog = DialogFactory.CreateStandardDialog(
            title: Language.Library_Update_SomeDownloadsFailed_Title,
            new StandardDialogParameters()
            {
                Text = string.Format(Language.Library_Update_SomeDownloadsFailed_Description, modsWhichFailed),
            },
            buttonDefinitions: [cancelFailedDownloads, retryFailedDownloads]
        );

        var result = await WindowManager.ShowDialog(allFailedDialog, DialogWindowType.Modal);
        if (result.ButtonId == retryFailedDownloads.Id)
        {
            // Only what failed: the rest was applied, and the user has already agreed to the replace.
            await ApplyUpdates(downloadFailures.Select(failure => failure.Update).ToArray(), replace, mayOpenBrowser, ct, confirmed: true);
        }
    }

    private async Task ShowSomeInstallsFailedDialog(IEnumerable<ModUpdateReplacement> replacements)
    {
        var failedToInstallItems = replacements
            .Where(replacement => replacement.Result != LibraryItemReplacementResult.Success)
            .Select(replacement => replacement.New)
            .ToList();

        var modNames = string.Join(Environment.NewLine, failedToInstallItems.Select(failed => failed.Name));
        var description = string.Format(Language.Library_Update_SomeInstallsFailed_Description, modNames);

        var deleteButtonId = ButtonDefinitionId.From("Delete");
        var keepButtonId = ButtonDefinitionId.From("Keep");

        var dialog = DialogFactory.CreateStandardDialog(
            title: Language.Library_Update_SomeInstallsFailed_Title,
            new StandardDialogParameters()
            {
                Text = description,
            },
            buttonDefinitions: [
                new DialogButtonDefinition(
                    Language.Library_Update_SomeInstallsFailed_DeleteFiles,
                    deleteButtonId,
                    ButtonAction.Accept,
                    ButtonStyling.Destructive
                ),
                new DialogButtonDefinition(
                    Language.Library_Update_SomeInstallsFailed_KeepFiles,
                    keepButtonId,
                    ButtonAction.Reject,
                    ButtonStyling.None
                )
            ]
        );

        var result = await WindowManager.ShowDialog(dialog, DialogWindowType.Modal);
        // User chose to delete the failed update files
        if (result.ButtonId == deleteButtonId)
            await _libraryService.RemoveLibraryItems(failedToInstallItems);
    }

    private async Task<(ButtonDefinitionId updateButtonId, StandardDialogResult dialogResult)> ShowInstalledInMultipleCollectionsDialog(IReadOnlyDictionary<CollectionGroup.ReadOnly, IReadOnlyList<(LibraryItem.ReadOnly libraryItem, LibraryLinkedLoadoutItem.ReadOnly linkedItem)>> collectionsAffected, int affectedCollectionCount)
    {
        var updateButtonId = ButtonDefinitionId.From("Update");
        var cancelButtonId = ButtonDefinitionId.From("Cancel");

        var dialogDesc = new StringBuilder()
            .AppendLine(Language.Library_Update_InstalledInMultipleCollections_Description1)
            .AppendLine();

        var collectionsGroupedByLoadout = collectionsAffected.Select(collection => collection.Key.AsLoadoutItemGroup().AsLoadoutItem())
            .GroupBy(item => item.Loadout.Name)
            .OrderBy(loadoutGroup => loadoutGroup.Key);

        foreach (var loadoutGroup in collectionsGroupedByLoadout)
        {
            dialogDesc.AppendLine($"{loadoutGroup.Key}:");
            foreach (var collection in loadoutGroup)
                dialogDesc.AppendLine($"{collection.Name}");
            
            dialogDesc.AppendLine();
        }

        dialogDesc.AppendLine(Language.Library_Update_InstalledInMultipleCollections_Description2);

        var confirmDialog = DialogFactory.CreateStandardDialog(
            title: Language.Library_Update_InstalledInMultipleCollections_Title,
            new StandardDialogParameters()
            {
                Text = dialogDesc.ToString(),
            },
            buttonDefinitions: [
                new DialogButtonDefinition(
                        Language.Library_Update_InstalledInMultipleCollections_Cancel,
                        cancelButtonId,
                        ButtonAction.Reject
                    ),
                    new DialogButtonDefinition(
                        string.Format(Language.Library_Update_InstalledInMultipleCollections_Ok, affectedCollectionCount),
                        updateButtonId,
                        ButtonAction.Accept,
                        ButtonStyling.Primary
                    )
            ]
        );

        var dialogResult = await WindowManager.ShowDialog(confirmDialog, DialogWindowType.Modal);
        return (updateButtonId, dialogResult);
    }

    private ValueTask HandleViewChangelogMessage(ViewChangelogMessage viewChangelogMessage, CancellationToken cancellationToken)
    {
/*
Note(sewer): This method currently sends you to the mod page due to technical limitations.
A summarised quote/explanation from myself below:

- On the site, there is no true 'per file' level changelog, but only a changelog at the mod page level currently.
    - The widget which shows the changes e.g. https://www.nexusmods.com/Core/Libs/Common/Widgets/ModChangeLogPopUp?mod_id=2347&game_id=1704&version=10.0.1 
      cannot be explicitly opened via hyperlink (due to a recent change on the site to block this).
    - From observation, I think those are automatically added when the user sets the version of the file to be the 
      same as the version of a changelog item made on the mod page.
- For rendering it myself: GraphQL API has a `changelogText` field for mod files.
    - When present, the values are listed in the form `#<ModChangelog:0x00007f060e21e880>` .
    - But there's no ModChangelog type or field anywhere in the autogenerated API docs.
    - I imagine you can manually hack this together by using V1 API, but code would be quickly obsolete.
- Some mod authors put the changelog in the file description; rather than on page level changelog.

After asking design, we're choosing to simply open the mod page for now.
*/
        return OpenSourcePage(viewChangelogMessage.Ids, static (source, item) => source.ChangelogUri(item));
    }

    private ValueTask HandleViewModPageMessage(ViewModPageMessage viewModPageMessage, CancellationToken cancellationToken)
    {
        return OpenSourcePage(viewModPageMessage.Ids, static (source, item) => source.PageUri(item));
    }

    private async ValueTask HandleDeleteItemMessage(DeleteItemMessage deleteItemMessage, CancellationToken cancellationToken)
    {
        var ids = deleteItemMessage.Ids;
        if (ids.Length == 0) return;
        
        var toRemove = ids.Select(id => LibraryItem.Load(_connection.Db, id)).ToArray();
        await LibraryItemRemover.RemoveAsync(_connection, _serviceProvider.GetRequiredService<IOverlayController>(), _libraryService, toRemove);

        _notificationService.ShowToast(Language.ToastNotification_Items_deleted);
    }
    
    private async ValueTask HandleDestinationMessage(DestinationMessage message, CancellationToken cancellationToken)
    {
        if (message.Ids.Length == 0) return;

        var actions = new LibraryDestinationActions(_libraryService, _connection, _loadout.InstallationInstance.Game.GameId);
        try
        {
            switch (message.Kind)
            {
                case DestinationActionKind.UseForThisGame:
                    await actions.UseForThisGame(message.Ids);
                    break;
                case DestinationActionKind.OnlyForThisGame:
                    await actions.OnlyForThisGame(message.Ids);
                    break;
                case DestinationActionKind.RemoveFromThisGame:
                    await actions.RemoveFromThisGame(message.Ids);
                    break;
                case DestinationActionKind.SuggestAGame:
                    await OfferSuggestedGame(actions, message.Ids);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(message), message.Kind, "Unhandled destination action");
            }
        }
        catch (UserFacingException refusal)
        {
            await _serviceProvider.GetRequiredService<IErrorPresenter>().Report(refusal, Language.LibraryDestination_ChangeActivity);
        }
    }

    /// <summary>Shows what the inferences found and applies it only when the user accepts.</summary>
    private async Task OfferSuggestedGame(LibraryDestinationActions actions, LibraryItemId[] ids)
    {
        var suggestion = actions.Suggest(ids);
        var markdown = _serviceProvider.GetRequiredService<IMarkdownRendererViewModel>();
        var buttons = new List<DialogButtonDefinition>();
        if (suggestion.IsUnknown)
        {
            markdown.Contents = Language.LibraryDestination_SuggestNone;
            buttons.Add(new DialogButtonDefinition("OK", ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Primary));
        }
        else
        {
            var names = new GameNames(_serviceProvider.GetServices<IGameData>());
            markdown.Contents = string.Format(Language.LibraryDestination_SuggestOffer, string.Join(", ", suggestion.Games.Select(names.NameOf).Order(StringComparer.CurrentCulture)));
            buttons.Add(new DialogButtonDefinition(Language.LibraryDestination_SuggestUse, ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Primary));
            buttons.Add(new DialogButtonDefinition("Cancel", ButtonDefinitionId.Cancel));
        }

        var dialog = DialogFactory.CreateStandardDialog(Language.LibraryDestination_SuggestTitle, new StandardDialogParameters { Markdown = markdown }, buttons.ToArray());
        var result = await WindowManager.ShowDialog(dialog, DialogWindowType.Modal);
        if (!suggestion.IsUnknown && result.ButtonId == ButtonDefinitionId.Accept)
            await actions.Accept(ids, suggestion);
    }

    private async ValueTask HandleHideUpdatesMessage(HideUpdatesMessage hideUpdatesMessage, CancellationToken cancellationToken)
    {
        var modUpdateFilterService = _serviceProvider.GetRequiredService<IModUpdateFilterService>();

        await hideUpdatesMessage.Id.Match(
            async modPageId =>
            {
                // Handle hiding/showing updates for a mod page
                // First get all library items we have that come from the mod page.
                var allLibraryFilesForThisMod = _connection.Db.Datoms(NexusModsLibraryItem.ModPageMetadata, modPageId)
                    .Select(datom => NexusModsLibraryItem.Load(_connection.Db, datom.E).FileMetadata)
                    .ToArray();

                // Note(sewer):
                // Behaviour per captainsandypants (Slack).
                // 'If any children have updates set to hidden, then the parent should have "Show updates" as the menu item.
                // When selected, this will set all children to show updates.'
                //
                // We have to be careful here.
                // We have a list of ***current versions*** of files in the mod page.
                // We need to check if it's possible to update ***any of these current versions*** to a new file.
                //     👉 👉 So for each file we need to check if any of its versions is not ignored as an update.
                var modsWithUpdatesHidden = allLibraryFilesForThisMod.Where(x =>
                    {
                        // Checking all versions is not a bug, it is intended behaviour per design.
                        // Search 'That definition also means older versions should be excluded from update checks.' in Slack.
                        var newerFiles = RunUpdateCheck.GetAllVersionsForExistingFile(x).ToArray();
                        return newerFiles.All(newer => modUpdateFilterService.IsFileHidden(newer.Uid));
                    }
                ).ToArray();

                if (modsWithUpdatesHidden.Length > 0)
                {
                    var allVersionsOfLibraryItemsWithUpdatesHidden =
                        modsWithUpdatesHidden.SelectMany(RunUpdateCheck.GetAllVersionsForExistingFile)
                                             .Select(x => x.Uid);
                    await modUpdateFilterService.ShowFilesAsync(allVersionsOfLibraryItemsWithUpdatesHidden);
                }
                else
                {
                    var allVersionsOfAllFilesForThisMod = allLibraryFilesForThisMod
                        .SelectMany(RunUpdateCheck.GetAllVersionsForExistingFile)
                        .Select(x => x.Uid);
                    await modUpdateFilterService.HideFilesAsync(allVersionsOfAllFilesForThisMod);
                }
            },
            async libraryItemId =>
            {
                // Handle hiding/showing updates for a single file
                var libraryItem = NexusModsLibraryItem.Load(_connection.Db, libraryItemId);
                var fileMetadata = libraryItem.FileMetadata;
                var allVersions = RunUpdateCheck.GetAllVersionsForExistingFile(fileMetadata).ToArray();

                var areAllHidden = allVersions.All(x => modUpdateFilterService.IsFileHidden(x.Uid));

                if (areAllHidden)
                    await modUpdateFilterService.ShowFilesAsync(allVersions.Select(x => x.Uid));
                else
                    await modUpdateFilterService.HideFilesAsync(allVersions.Select(x => x.Uid));
            }
        );
    }

    private ValueTask OpenSourcePage(LibraryItemId[] ids, Func<IModSource, LibraryItem.ReadOnly, Uri?> uriOf)
    {
        var resolver = _serviceProvider.GetRequiredService<ModSourceResolver>();
        var os = _serviceProvider.GetRequiredService<IOSInterop>();
        var uri = ModSourcePages.Find(_connection.Db, resolver, ids, uriOf);
        if (uri is not null) os.OpenUri(uri);
        return ValueTask.CompletedTask;
    }

    // Note(sewer): ValueTask because of R3 constraints with ReactiveCommand API
    private async ValueTask RefreshUpdates(bool force, CancellationToken token)
    {
        var sources = _serviceProvider.GetRequiredService<ModSourceResolver>().Sources;
        var game = _loadout.InstallationInstance.Game;
        var logger = _serviceProvider.GetRequiredService<ILogger<LibraryViewModel>>();

        // One source being offline must not stop the others from checking.
        foreach (var source in sources)
        {
            try
            {
                await source.CheckUpdates(game, force, token);
            }
            // A timeout is an OperationCanceledException too; only the command's own token means stop.
            catch (Exception e) when (token.IsCancellationRequested is false)
            {
                logger.LogWarning(e, "Checking {Source} for updates failed", source.DisplayName);
            }
        }
    }

    private async ValueTask InstallItems(LibraryItemId[] ids, LoadoutItemGroupId targetLoadoutGroup, bool useAdvancedInstaller, CancellationToken cancellationToken)
    {
        var db = _connection.Db;
        var items = ids
            .Select(id => LibraryItem.Load(db, id))
            .Where(x => x.IsValid() && IsInstallableAsMod(x))
            .ToArray();

        await Parallel.ForAsync(
            fromInclusive: 0,
            toExclusive: items.Length,
            body: (i, innerCancellationToken) => InstallLibraryItem(items[i], _loadout, targetLoadoutGroup, innerCancellationToken, useAdvancedInstaller),
            parallelOptions: new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken }
        );
        
        var targetCollection = LoadoutItem.Load(db, targetLoadoutGroup);
    }

    // A modlist row still carries its library item so it can be selected and deleted, but installing the
    // .wabbajack itself as a mod would put the zip's manifest into the loadout. The list's page installs it.
    internal static bool IsInstallableAsMod(LibraryItem.ReadOnly item)
        => !(item.TryGetAsLibraryFile(out var file) && file.TryGetAsWabbajackModlistLibraryFile(out _));

    // A modlist's group stays disabled until its install is complete and belongs to the list, not the user.
    internal static bool IsInstallTarget(CollectionGroup.ReadOnly group) => !group.TryGetAsWabbajackModlistLoadoutGroup(out _);

    private LibraryItemId[] GetSelectedIds()
    {
        var ids = Adapter.SelectedModels
            .SelectMany(static model => GetLibraryItemIds(model))
            .Distinct()
            .ToArray();

        return ids;
    }
    
    private static IEnumerable<LibraryItemId> GetLibraryItemIds(CompositeItemModel<EntityId> itemModel)
    {
        return itemModel.Get<LibraryComponents.LibraryItemIds>(LibraryColumns.Actions.LibraryItemIdsComponentKey).ItemIds;
    }

    private IEnumerable<CompositeItemModel<EntityId>> GetSelectedModelsWithUpdates()
    {
        return Adapter.SelectedModels
            .Where(model =>
            {
                var updateComponent = model.GetOptional<LibraryComponents.UpdateAction>(LibraryColumns.Actions.UpdateComponentKey);
                return updateComponent.HasValue;
            });
    }

    private LoadoutItemGroupId GetInstallationTarget() => (SelectedInstallationTarget?.Id ?? _installationTargets[0].Id).Value;

    private ValueTask InstallSelectedItems(bool useAdvancedInstaller, CancellationToken cancellationToken)
    {
        return InstallItems(GetSelectedIds(), GetInstallationTarget(), useAdvancedInstaller, cancellationToken);
    }

    private async ValueTask InstallLibraryItem(
        LibraryItem.ReadOnly libraryItem,
        LoadoutId loadout,
        LoadoutItemGroupId targetLoadoutGroup,
        CancellationToken cancellationToken,
        bool useAdvancedInstaller = false)
    {
        try
        {
            await _loadoutManager.InstallItem(libraryItem, loadout, parent: targetLoadoutGroup, installer: useAdvancedInstaller ? _advancedInstaller : null);
            var targetCollection  = LoadoutItem.Load(_connection.Db, targetLoadoutGroup);
        }
        catch (OperationCanceledException)
        {
            // User cancelled the installation - this is expected behavior, don't show error
            var logger = _serviceProvider.GetRequiredService<ILogger<LibraryViewModel>>();
            logger.LogInformation("Installation of {LibraryItem} was cancelled by user", libraryItem.Name);
        }
    }

    private async ValueTask RemoveSelectedItems(CancellationToken cancellationToken)
    {
        var db = _connection.Db;
        var toRemove = GetSelectedIds().Select(id => LibraryItem.Load(db, id)).ToArray();
        await LibraryItemRemover.RemoveAsync(_connection, _serviceProvider.GetRequiredService<IOverlayController>(), _libraryService, toRemove);
        
        _notificationService.ShowToast(Language.ToastNotification_Items_deleted);
    }

    // The command only runs while a provider is set, but it can be cleared between the check and the run.
    internal static IStorageProvider RequireStorageProvider(IStorageProvider? storageProvider)
    {
        return storageProvider ?? throw new FilePickerUnavailableException();
    }

    private async ValueTask AddFilesFromDisk(IStorageProvider storageProvider, CancellationToken cancellationToken)
    {
        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            Title = Language.LoadoutGridView_AddMod_FilePicker_Title,
            FileTypeFilter =
            [
                // TODO: fetch from some service
                new FilePickerFileType(Language.LoadoutGridView_AddMod_FileType_Archive)
                {
                    Patterns = ["*.zip", "*.7z", "*.rar"],
                },
            ],
        });

        var paths = files
            .Select(file => file.TryGetLocalPath())
            .NotNull()
            .Select(path => FileSystem.Shared.FromUnsanitizedFullPath(path))
            .Where(path => path.FileExists)
            .ToArray();

        await AddFilesToLibrary(paths, cancellationToken);
    }

    /// <summary>Adds the files to the library for this loadout's game.</summary>
    internal async ValueTask AddFilesToLibrary(AbsolutePath[] paths, CancellationToken cancellationToken)
    {
        await Parallel.ForAsync(
            fromInclusive: 0,
            toExclusive: paths.Length,
            body: async (i, innerCancellationToken) =>
            {
                var path = paths[i];
                await _libraryService.AddLocalFile(path, new LoadoutAcquisition(_loadout.LoadoutId));
            },
            parallelOptions: new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken }
        );
    }

    private ValueTask UpdateSelectedItems(CancellationToken cancellationToken)
        => UpdateSelectedItemsInternal(replace: true, cancellationToken);

    private ValueTask UpdateAndKeepOldSelectedItems(CancellationToken cancellationToken)
        => UpdateSelectedItemsInternal(replace: false, cancellationToken);

    private ValueTask UpdateSelectedItemsInternal(bool replace, CancellationToken cancellationToken)
    {
        // Note(sewer): The selection count is not guaranteed to equal the number of items
        // with updates because the user can select a mixture of items with and without updates
        // in a single selection.
        var updates = GetSelectedModelsWithUpdates()
            .Select(model => model.Get<LibraryComponents.UpdateAction>(LibraryColumns.Actions.UpdateComponentKey).Update.Value)
            .ToArray();

        return ApplyUpdates(updates, replace, mayOpenBrowser: true, cancellationToken);
    }

    private async ValueTask UpdateAllItems(CancellationToken cancellationToken)
    {
        IsUpdatingAll = true;
        try
        {
            // Only the updates for files destined for the current game
            var game = _loadout.InstallationInstance.Game;
            var allUpdates = _serviceProvider.GetRequiredService<ModSourceResolver>().Sources
                .SelectMany(source => source.AllUpdates(game))
                .ToArray();

            await ApplyUpdates(allUpdates, replace: true, mayOpenBrowser: false, cancellationToken);
        }
        finally
        {
            IsUpdatingAll = false;
        }
    }

    /// <summary>
    /// Removes old library items that are not installed in any read-only (e.g. Nexus) collections.
    /// This ensures collections 'remain' installed.
    /// 
    /// Old items where the new library item is invalid (e.g. Replace Failed), are preserved;
    /// cleaning those up happens in <see cref="ShowSomeInstallsFailedDialog"/> in partial fail scenarios.
    /// </summary>
    /// <param name="oldToNewLibraryMapping">The mapping of old library items to their new replacements.</param>
    private async ValueTask RemoveOldLibraryItemsIfNotInReadOnlyCollections(IEnumerable<ModUpdateReplacement> oldToNewLibraryMapping)
    {
        var oldItemsToRemove = new List<LibraryItem.ReadOnly>();
        
        foreach (var item in oldToNewLibraryMapping)
        {
            // If the new item is not valid (e.g. replace failed), and user chose to 'delete new files' we skip it.
            if (item.New.IsValid() is false)
                continue;
            
            // Check if the old item is still linked to any collections
            var collectionsWithItem = _libraryService.CollectionsWithLibraryItem(item.Old, excludeReadOnlyCollections: false);
            
            // Only remove if the item is NOT in any read-only collections (i.e., only in modifiable collections or no collections)
            var hasReadOnlyCollections = collectionsWithItem.Any(x => x.collection.IsReadOnly);
            if (!hasReadOnlyCollections)
                oldItemsToRemove.Add(item.Old);
        }

        if (oldItemsToRemove.Count > 0)
            await _libraryService.RemoveLibraryItems(oldItemsToRemove);
    }
}

public readonly record struct InstallMessage(LibraryItemId[] Ids);
public readonly record struct UpdateAndReplaceMessage(ModUpdate Update, CompositeItemModel<EntityId> TreeNode);
public readonly record struct UpdateAndKeepOldMessage(ModUpdate Update, CompositeItemModel<EntityId> TreeNode);
public readonly record struct ViewChangelogMessage(LibraryItemId[] Ids);
public readonly record struct ViewModPageMessage(LibraryItemId[] Ids);
public readonly record struct HideUpdatesMessage(OneOf<NexusModsModPageMetadataId, NexusModsLibraryItemId> Id);
public readonly record struct DeleteItemMessage(LibraryItemId[] Ids);
public readonly record struct DestinationMessage(LibraryItemId[] Ids, DestinationActionKind Kind);

public class LibraryTreeDataGridAdapter :
    TreeDataGridAdapter<CompositeItemModel<EntityId>, EntityId>,
    ITreeDataGirdMessageAdapter<OneOf<InstallMessage, UpdateAndReplaceMessage, UpdateAndKeepOldMessage, ViewChangelogMessage, ViewModPageMessage, HideUpdatesMessage, DeleteItemMessage, DestinationMessage>>
{
    private readonly ILibraryDataProvider[] _libraryDataProviders;
    private readonly LibraryFilter _libraryFilter;
    private readonly IConnection _connection;

    public Subject<OneOf<InstallMessage, UpdateAndReplaceMessage, UpdateAndKeepOldMessage, ViewChangelogMessage, ViewModPageMessage, HideUpdatesMessage, DeleteItemMessage, DestinationMessage>> MessageSubject { get; } = new();

    public LibraryTreeDataGridAdapter(IServiceProvider serviceProvider, LibraryFilter libraryFilter) : base(serviceProvider)
    {
        _libraryFilter = libraryFilter;
        _libraryDataProviders = serviceProvider.GetServices<ILibraryDataProvider>().ToArray();
        _connection = serviceProvider.GetRequiredService<IConnection>();
    }

    protected override IObservable<IChangeSet<CompositeItemModel<EntityId>, EntityId>> GetRootsObservable(bool viewHierarchical)
    {
        return _libraryDataProviders.Select(x => x.ObserveLibraryItems(_libraryFilter)).MergeChangeSets();
    }

    protected override void BeforeModelActivationHook(CompositeItemModel<EntityId> model)
    {
        base.BeforeModelActivationHook(model);

        model.SubscribeToComponentAndTrack<LibraryComponents.InstallAction, LibraryTreeDataGridAdapter>(
            key: LibraryColumns.Actions.InstallComponentKey,
            state: this,
            factory: static (self, itemModel, component) => component.CommandInstall.Subscribe((self, itemModel, component), static (_, state) =>
            {
                var (self, model, component) = state;
                var ids = GetLibraryItemIds(model).ToArray();

                self.MessageSubject.OnNext(new InstallMessage(ids));
            })
        );

        model.SubscribeToComponentAndTrack<LibraryComponents.UpdateAction, LibraryTreeDataGridAdapter>(
            key: LibraryColumns.Actions.UpdateComponentKey,
            state: this,
            factory: static (self, itemModel, component) => component.CommandUpdateAndKeepOld
                .SubscribeOnUIThreadDispatcher() // Update payload may expand row for free users, requiring UI thread.
                .Subscribe((self, itemModel, component), static (_, state) =>
            {
                var (self, model, component) = state;
                self.MessageSubject.OnNext(new UpdateAndKeepOldMessage(component.Update.Value, model));
            })
        );

        model.SubscribeToComponentAndTrack<LibraryComponents.UpdateAction, LibraryTreeDataGridAdapter>(
            key: LibraryColumns.Actions.UpdateComponentKey,
            state: this,
            factory: static (self, itemModel, component) => component.CommandUpdateAndReplace
                .SubscribeOnUIThreadDispatcher() // Update payload may expand row for free users, requiring UI thread.
                .Subscribe((self, itemModel, component), static (_, state) =>
            {
                var (self, model, component) = state;
                self.MessageSubject.OnNext(new UpdateAndReplaceMessage(component.Update.Value, model));
            })
        );

        // Hiding updates still works on Nexus ids; it moves onto IModSource with the hide action.
        static OneOf<NexusModsModPageMetadataId, NexusModsLibraryItemId> GetHideUpdatesTarget(IDb db, EntityId entityId)
        {
            var modPageMetadata = NexusModsModPageMetadata.Load(db, entityId);
            if (modPageMetadata.IsValid())
                return OneOf<NexusModsModPageMetadataId, NexusModsLibraryItemId>.FromT0(modPageMetadata.Id);
                
            // Try to load as NexusModsLibraryItem
            var libraryItem = NexusModsLibraryItem.Load(db, entityId);
            if (libraryItem.IsValid())
                return OneOf<NexusModsModPageMetadataId, NexusModsLibraryItemId>.FromT1(libraryItem.Id);
            
            throw new Exception("Unknown type of entity for HideUpdatesAction: " + entityId);
        }

        model.SubscribeToComponentAndTrack<LibraryComponents.ViewChangelogAction, LibraryTreeDataGridAdapter>(
            key: LibraryColumns.Actions.ViewChangelogComponentKey,
            state: this,
            factory: static (self, itemModel, component) => component.CommandViewChangelog.Subscribe((self, itemModel, component), static (_, state) =>
            {
                var (self, model, _) = state;

                self.MessageSubject.OnNext(new ViewChangelogMessage(GetLibraryItemIds(model).ToArray()));
            })
        );

        model.SubscribeToComponentAndTrack<SharedComponents.ViewModPageAction, LibraryTreeDataGridAdapter>(
            key: LibraryColumns.Actions.ViewModPageComponentKey,
            state: this,
            factory: static (self, itemModel, component) => component.CommandViewModPage.Subscribe((self, itemModel, component), static (_, state) =>
            {
                var (self, model, _) = state;

                self.MessageSubject.OnNext(new ViewModPageMessage(GetLibraryItemIds(model).ToArray()));
            })
        );
        
        model.SubscribeToComponentAndTrack<LibraryComponents.DeleteItemAction, LibraryTreeDataGridAdapter>(
            key: LibraryColumns.Actions.DeleteItemComponentKey,
            state: this,
            factory: static (self, itemModel, component) => component.CommandDeleteItem.Subscribe((self, itemModel, component), static (_, state) =>
            {
                var (self, model, component) = state;
                var ids = GetLibraryItemIds(model).ToArray();

                self.MessageSubject.OnNext(new DeleteItemMessage(ids));
            })
        );

        model.SubscribeToComponentAndTrack<LibraryComponents.DestinationActions, LibraryTreeDataGridAdapter>(
            key: LibraryColumns.Actions.DestinationActionsComponentKey,
            state: this,
            factory: static (self, itemModel, component) => component.Requested.Subscribe((self, itemModel), static (kind, state) =>
            {
                var (self, model) = state;
                self.MessageSubject.OnNext(new DestinationMessage(GetLibraryItemIds(model).ToArray(), kind));
            })
        );

        model.SubscribeToComponentAndTrack<LibraryComponents.HideUpdatesAction, LibraryTreeDataGridAdapter>(
            key: LibraryColumns.Actions.HideUpdatesComponentKey,
            state: this,
            factory: static (self, itemModel, component) => component.CommandHideUpdates.Subscribe((self, itemModel, component), static (_, state) =>
            {
                var (self, model, _) = state;
                var entityId = model.Key;

                self.MessageSubject.OnNext(new HideUpdatesMessage(GetHideUpdatesTarget(self._connection.Db, entityId)));
            })
        );
    }
    
    private static IEnumerable<LibraryItemId> GetLibraryItemIds(CompositeItemModel<EntityId> itemModel)
    {
        return itemModel.Get<LibraryComponents.LibraryItemIds>(LibraryColumns.Actions.LibraryItemIdsComponentKey).ItemIds;
    }

    protected override IColumn<CompositeItemModel<EntityId>>[] CreateColumns(bool viewHierarchical)
    {
        var nameColumn = ColumnCreator.Create<EntityId, SharedColumns.Name>();

        return
        [
            viewHierarchical ? ITreeDataGridItemModel<CompositeItemModel<EntityId>, EntityId>.CreateExpanderColumn(nameColumn) : nameColumn,
            ColumnCreator.Create<EntityId, LibraryColumns.ItemVersion>(),
            ColumnCreator.Create<EntityId, SharedColumns.Source>(),
            ColumnCreator.Create<EntityId, SharedColumns.ItemSize>(),
            ColumnCreator.Create<EntityId, LibraryColumns.DownloadedDate>(sortDirection: ListSortDirection.Descending),
            ColumnCreator.Create<EntityId, SharedColumns.InstalledDate>(),
            ColumnCreator.Create<EntityId, LibraryColumns.Collections>(),
            ColumnCreator.Create<EntityId, LibraryColumns.Destination>(),
            ColumnCreator.Create<EntityId, LibraryColumns.Actions>(),
        ];
    }

    private bool _isDisposed;
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_isDisposed)
        {
            MessageSubject.Dispose();
            _isDisposed = true;
        }

        base.Dispose(disposing);
    }
}
