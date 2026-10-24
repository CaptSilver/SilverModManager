using System.Reactive.Linq;
using Avalonia.Media.Imaging;
using DynamicData;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Extensions;
using SilverModManager.App.UI.Helpers;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Collections;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.IndexSegments;
using SilverModManager.MnemonicDB.Abstractions.Query;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Jobs;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;
using SilverModManager.UI.Sdk.Icons;
using R3;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using ReactiveCommand = R3.ReactiveCommand;

namespace SilverModManager.App.UI.Pages.LoadoutPage;

public partial class CollectionLoadoutViewModel : APageViewModel<ICollectionLoadoutViewModel>, ICollectionLoadoutViewModel
{
    public LoadoutTreeDataGridAdapter Adapter { get; }

    public CollectionLoadoutViewModel(
        IWindowManager windowManager,
        IServiceProvider serviceProvider,
        CollectionLoadoutPageContext pageContext) : base(windowManager)
    {
        var connection = serviceProvider.GetRequiredService<IConnection>();
        var toastNotificationService = serviceProvider.GetRequiredService<IWindowNotificationService>();

        var tilePipeline = ImagePipelines.GetCollectionTileImagePipeline(serviceProvider);
        var backgroundPipeline = ImagePipelines.GetCollectionBackgroundImagePipeline(serviceProvider);
        var userAvatarPipeline = ImagePipelines.GetUserAvatarPipeline(serviceProvider);
        
        var nexusCollectionGroup = NexusCollectionLoadoutGroup.Load(connection.Db, pageContext.GroupId);
        var group = nexusCollectionGroup.AsCollectionGroup();
        TabIcon = IconValues.CollectionsOutline;
        TabTitle = group.AsLoadoutItemGroup().AsLoadoutItem().Name;

        IsCollectionEnabled = !group.AsLoadoutItemGroup().AsLoadoutItem().IsDisabled;
        IsReadOnly = group.IsReadOnly;

        var revisionMetadata = pageContext.RevisionId.HasValue
            ? CollectionRevisionMetadata.Load(connection.Db, pageContext.RevisionId.Value)
            : Optional<CollectionRevisionMetadata.ReadOnly>.None;

        if (revisionMetadata.HasValue)
        {
            Name = revisionMetadata.Value.Collection.Name;
            RevisionNumber = revisionMetadata.Value.RevisionNumber;
            AuthorName = revisionMetadata.Value.Collection.Author.Name;
            IsLocalCollection = false;
            
            EndorsementCount = revisionMetadata.Value.Collection.Endorsements.ValueOr(0ul);
            TotalDownloads = revisionMetadata.Value.Collection.TotalDownloads.ValueOr(0ul);
            TotalSize = revisionMetadata.Value.TotalSize.ValueOr(Size.Zero);
            OverallRating = Percent.CreateClamped(revisionMetadata.Value.OverallRating.ValueOr(0));
        }
        else
        {
            Name = TabTitle;
            RevisionNumber = RevisionNumber.From(1);
            AuthorName = string.Empty;
            IsLocalCollection = true;
        }

        var loadoutFilter = new LoadoutFilter
        {
            LoadoutId = pageContext.LoadoutId,
            CollectionGroupId = LoadoutItemGroupId.From(pageContext.GroupId),
        };

        Adapter = new LoadoutTreeDataGridAdapter(serviceProvider, loadoutFilter);

        CommandToggle = new ReactiveCommand(
            executeAsync: async (_, _) =>
            {
                using var tx = connection.BeginTransaction();

                var shouldEnable = !IsCollectionEnabled;
                if (shouldEnable)
                {
                    tx.Retract(pageContext.GroupId, LoadoutItem.Disabled, Null.Instance);
                } else
                {
                    tx.Add(pageContext.GroupId, LoadoutItem.Disabled, Null.Instance);
                }

                await tx.Commit();
            },
            awaitOperation: AwaitOperation.Drop,
            configureAwait: false
        );
        
        CommandDeleteCollection = new ReactiveCommand(
            executeAsync: async (_, _) =>
            {
                var workspaceController = GetWorkspaceController();
                await CollectionDeleteHelpers.DeleteCollectionAsync(nexusCollectionGroup.AsCollectionGroup(), serviceProvider.GetRequiredService<ILoadoutManager>(), workspaceController, connection, toastNotificationService);
            },
            awaitOperation: AwaitOperation.Drop,
            configureAwait: false
        );

        var canReinstall = !IsLocalCollection && IsReadOnly;
        CommandReinstallCollection = _isInstalling.Select(isInstalling => canReinstall && !isInstalling).ToReactiveCommand<Unit>(
            executeAsync: async (_, _) =>
            {
                var dialog = DialogFactory.CreateStandardDialog(
                    Language.CollectionLoadoutView_ReinstallCollection_Confirmation_Title,
                    new StandardDialogParameters()
                    {
                        Text = string.Format(Language.CollectionLoadoutView_ReinstallCollection_Confirmation_Message, TabTitle),
                    },
                    [
                        DialogStandardButtons.Cancel,
                        new DialogButtonDefinition(
                            Language.CollectionLoadoutView_ReinstallCollection_Confirmation_Button,
                            ButtonDefinitionId.Accept,
                            ButtonAction.Accept,
                            ButtonStyling.Primary
                        ),
                    ]
                );
                var result = await WindowManager.ShowDialog(dialog, DialogWindowType.Modal);
                if (result.ButtonId != ButtonDefinitionId.Accept) return;

                // This page closes partway through, so every outcome is reported as a toast rather than left to the command.
                try
                {
                    var job = await CollectionReinstaller.Reinstall(
                        serviceProvider,
                        nexusCollectionGroup,
                        // The download page shows the install progress, so the open tabs move there before the group goes away.
                        beforeRemove: async () => await CollectionDeleteHelpers.ReplaceInstalledPagesWithDownloadPageAsync(nexusCollectionGroup, GetWorkspaceController())
                    );

                    var newGroup = await job;
                    if (CollectionDownloader.IsFullyInstalled(job.JobDefinition.Items, newGroup.AsCollectionGroup(), connection.Db))
                        toastNotificationService.ShowToast(Language.ToastNotification_Collection_installed, ToastNotificationVariant.Success);
                }
                catch (OperationCanceledException)
                {
                    // Cancelled from the download page, which shows it.
                }
                catch (Exception e)
                {
                    toastNotificationService.ShowToast(
                        string.Format(Language.CollectionLoadoutView_ReinstallCollection_Failed, e.Message),
                        ToastNotificationVariant.Failure
                    );
                }
            },
            awaitOperation: AwaitOperation.Drop,
            configureAwait: false
        );

        CommandShowDownloads = new ReactiveCommand(execute: _ => SelectedTab = CollectionPageTab.Downloads);

        CommandOpenOnNexusMods = new ReactiveCommand(execute: _ =>
        {
            if (!revisionMetadata.HasValue) throw new InvalidOperationException("A local collection has no page on Nexus Mods");

            var collection = revisionMetadata.Value.Collection;
            var gameDomain = serviceProvider.GetRequiredService<IGameDomainToGameIdMappingCache>()[collection.GameId];
            serviceProvider.GetRequiredService<IOSInterop>().OpenUri(CollectionPageTabs.NexusModsUri(gameDomain, collection.Slug, revisionMetadata.Value.RevisionNumber));
        });

        // The download page reads the collection's archive from the library, which can be removed while the collection stays installed.
        if (revisionMetadata.HasValue && HasCollectionLibraryFile(connection.Db, revisionMetadata.Value))
        {
            Downloads = new CollectionDownloadViewModel(
                windowManager: windowManager,
                serviceProvider: serviceProvider,
                revisionMetadata: revisionMetadata.Value,
                targetLoadout: pageContext.LoadoutId,
                isEmbedded: true
            );
        }

        CommandMakeLocalEditableCopy = new ReactiveCommand(
            executeAsync: async (_, _) =>
            {
                var dialog = DialogFactory.CreateStandardDialog(
                    "Collection Name",
                    new StandardDialogParameters()
                    {
                        Text = "This is the name of the new cloned collection.",
                        InputLabel = "Collection name",
                        InputWatermark = "(Local) " + group.AsLoadoutItemGroup().AsLoadoutItem().Name,
                        InputText = "(Local) " + group.AsLoadoutItemGroup().AsLoadoutItem().Name,
                    },
                    [
                        DialogStandardButtons.Cancel,
                        new DialogButtonDefinition(
                            "Create",
                            ButtonDefinitionId.Accept,
                            ButtonAction.Accept,
                            ButtonStyling.Primary
                        ),
                    ]
                );
                var result = await WindowManager.ShowDialog(dialog, DialogWindowType.Modal);
                if (result.ButtonId != ButtonDefinitionId.Accept || string.IsNullOrWhiteSpace(result.InputText))
                    return;
                
                var cloneId = await CollectionCreator.MakeEditableLocalCollection(serviceProvider.GetRequiredService<ILoadoutManager>(), group.Db.Connection, group.Id, result.InputText);

                var pageData = new PageData
                {
                    FactoryId = LoadoutPageFactory.StaticId,
                    Context = new LoadoutPageContext()
                    {
                        LoadoutId = group.AsLoadoutItemGroup().AsLoadoutItem().LoadoutId, 
                        GroupScope = CollectionGroupId.From(cloneId),
                    },
                };

                var workspaceController = GetWorkspaceController();
                var behavior = workspaceController.GetOpenPageBehavior(pageData, NavigationInformation.From(OpenPageBehaviorType.ReplaceTab));
                workspaceController.OpenPage(WorkspaceId, pageData, behavior);
            }
        );


        this.WhenActivated(disposables =>
        {
            Adapter.Activate().AddTo(disposables);

            // The embedded download page navigates and retitles through the tab it sits in.
            if (Downloads is CollectionDownloadViewModel downloads)
            {
                downloads.WindowId = WindowId;
                downloads.WorkspaceId = WorkspaceId;
                downloads.PanelId = PanelId;
                downloads.TabId = TabId;
            }
            
            connection.ObserveDatoms(LoadoutItem.ParentId, pageContext.GroupId)
                .QueryWhenChanged(datoms => datoms.Count)
                .OnUI()
                .Subscribe(count => InstalledModsCount = count)
                .AddTo(disposables);
            
            LoadoutItem
                .Observe(connection, pageContext.GroupId)
                .Select(static item => !item.IsDisabled)
                .OnUI()
                .Subscribe(isEnabled => IsCollectionEnabled = isEnabled)
                .AddTo(disposables);

            if (revisionMetadata.HasValue)
            {
                var revisionId = revisionMetadata.Value.Id;
                serviceProvider.GetRequiredService<IJobMonitor>()
                    .HasActiveJob<InstallCollectionJob>(job => job.RevisionMetadata.Id == revisionId && job.TargetLoadout == pageContext.LoadoutId)
                    .OnUI()
                    .Subscribe(isInstalling => _isInstalling.Value = isInstalling)
                    .AddTo(disposables);

                ImagePipelines
                    .CreateObservable(revisionMetadata.Value.CollectionId, tilePipeline)
                    .ObserveOnUIThreadDispatcher()
                    .Subscribe(this, static (image, self) => self.TileImage = image)
                    .AddTo(disposables);

                ImagePipelines
                    .CreateObservable(revisionMetadata.Value.CollectionId, backgroundPipeline)
                    .ObserveOnUIThreadDispatcher()
                    .Subscribe(this, static (image, self) => self.BackgroundImage = image)
                    .AddTo(disposables);

                ImagePipelines
                    .CreateObservable(revisionMetadata.Value.Collection.AuthorId, userAvatarPipeline)
                    .ObserveOnUIThreadDispatcher()
                    .Subscribe(this, static (image, self) => self.AuthorAvatar = image)
                    .AddTo(disposables);
            }

            Adapter.MessageSubject.SubscribeAwait(async (message, cancellationToken) =>
            {
                await message.Match<Task>(
                    toggleEnableStateMessage => 
                        LoadoutViewModel.HandleToggleItemEnabledState(toggleEnableStateMessage.Ids, connection),
                    openCollectionMessage =>
                    {
                        LoadoutViewModel.HandleOpenItemCollectionPage(openCollectionMessage.Ids, 
                            openCollectionMessage.NavigationInformation, 
                            pageContext.LoadoutId, GetWorkspaceController(), connection);
                        return Task.CompletedTask;
                    },
                    viewModPageMessage =>
                    {
                        LoadoutViewModel.HandleOpenModPageFor(viewModPageMessage.Ids, connection,  
                            serviceProvider.GetRequiredService<IOSInterop>(),
                            cancellationToken);
                        return Task.CompletedTask;
                    },
                    viewModFilesMessage =>
                    {
                        LoadoutViewModel.HandleViewModFiles(viewModFilesMessage.Ids, 
                            viewModFilesMessage.NavigationInformation, 
                            connection, GetWorkspaceController());
                        return Task.CompletedTask;
                    },
                    uninstallItemMessage => LoadoutViewModel.HandleUninstallItem(uninstallItemMessage.Ids, windowManager, connection),
                    toggleProtectedPathsMessage => LoadoutViewModel.HandleToggleProtectedPathsAllowed(toggleProtectedPathsMessage.Ids, connection)
                );
            }, awaitOperation: AwaitOperation.Parallel, configureAwait: false).AddTo(disposables);
        });
    }

    public bool IsLocalCollection { get; }
    public bool IsReadOnly { get; }

    // Reinstalling while an install of this collection runs would remove the group under it.
    private readonly R3.ReactiveProperty<bool> _isInstalling = new(false);

    public string Name { get; }
    public ulong EndorsementCount { get; }
    public ulong TotalDownloads { get; }
    public Size TotalSize { get; }
    public Percent OverallRating { get; }

    public RevisionNumber RevisionNumber { get; }

    public string AuthorName { get; }

    [Reactive] public partial Bitmap? AuthorAvatar { get; private set; }

    [Reactive] public partial Bitmap? BackgroundImage { get; private set; }

    [Reactive] public partial Bitmap? TileImage { get; private set; }

    [Reactive] public partial bool IsCollectionEnabled { get; private set; }
    
    [Reactive] public partial int InstalledModsCount { get; private set; }
    
    public ReactiveCommand<Unit> CommandToggle { get; }
    public ReactiveCommand<Unit> CommandDeleteCollection { get; }
    
    public ReactiveCommand<Unit> CommandMakeLocalEditableCopy { get; }
    public ReactiveCommand<Unit> CommandReinstallCollection { get; }
    public ReactiveCommand<Unit> CommandOpenOnNexusMods { get; }
    public ReactiveCommand<Unit> CommandShowDownloads { get; }
    public ICollectionDownloadViewModel? Downloads { get; }

    [Reactive] public partial CollectionPageTab SelectedTab { get; set; }

    private static bool HasCollectionLibraryFile(IDb db, CollectionRevisionMetadata.ReadOnly revisionMetadata)
    {
        return db.Datoms(
            (NexusModsCollectionLibraryFile.CollectionSlug, revisionMetadata.Collection.Slug),
            (NexusModsCollectionLibraryFile.CollectionRevisionNumber, revisionMetadata.RevisionNumber)
        ).Count > 0;
    }
}
