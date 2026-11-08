using SilverModManager.Sdk.Errors;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using R3;
using ReactiveUI;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Extensions;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Helpers;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Collections;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.IndexSegments;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Resources;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;
using SilverModManager.UI.Sdk.Icons;
using BehaviorSubject = System.Reactive.Subjects.BehaviorSubject<SilverModManager.App.UI.Pages.CollectionDownload.CollectionInstructions>;
using Disposable = System.Reactive.Disposables.Disposable;
using Observable = System.Reactive.Linq.Observable;
using ReactiveCommand = ReactiveUI.ReactiveCommand;

using SilverModManager.App.UI.Pages.Collections.Update;

namespace SilverModManager.App.UI.Pages.Collections;

/// <summary>Reads the collection.json out of a collection's archive.</summary>
public delegate ValueTask<CollectionRoot> CollectionJsonParser(NexusModsCollectionLibraryFile.ReadOnly file, CancellationToken cancellationToken);

/// <summary>
/// A Nexus Mods collection, either installed in the loadout or only known from its revision in the library.
/// </summary>
public sealed class NexusCollectionSource : CollectionSource
{
    private readonly LoadoutId _loadout;
    private readonly CollectionRevisionMetadata.ReadOnly _revision;
    private readonly NexusCollectionLoadoutGroup.ReadOnly? _group;
    private readonly CollectionJsonParser? _parse;
    private readonly BehaviorSubject _instructions = new(CollectionInstructions.None);
    private readonly Lazy<CollectionLink> _link;

    private NexusCollectionSource(
        IServiceProvider services,
        LoadoutId loadout,
        CollectionRevisionMetadata.ReadOnly revision,
        NexusCollectionLoadoutGroup.ReadOnly? group,
        CollectionJsonParser? parse) : base(services, loadout)
    {
        _loadout = loadout;
        _revision = revision;
        _group = group;
        _parse = parse;
        _link = new Lazy<CollectionLink>(CreateLink);

        var collection = revision.Collection;
        AuthorName = collection.Author.Name;
        VersionText = NexusCollectionUpdate.VersionText(revision.RevisionNumber);
        // Every number reads as zero when Nexus left it out, so the header renders as it always has.
        Stats = new CollectionStats(
            Rating: Percent.CreateClamped(revision.OverallRating.ValueOr(0)),
            Endorsements: collection.Endorsements.ValueOr(0ul),
            Downloads: collection.TotalDownloads.ValueOr(0ul),
            TotalSize: revision.TotalSize.ValueOr(Size.Zero),
            ArchiveCount: null);

        if (group is { } installed)
        {
            var groupId = installed.AsCollectionGroup().CollectionGroupId;
            InitialName = installed.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().Name;
            Name = LoadoutItem.Observe(Connection, groupId).Select(item => item.Name);
            // Constant on purpose. An update switches the group to the new revision in place, and the old revision's
            // observable would then report no group, which would take the Installed tab from an installed collection.
            Group = Observable.Return(Optional<CollectionGroupId>.Create(groupId));
            IsReadOnly = installed.AsCollectionGroup().IsReadOnly;
        }
        else
        {
            InitialName = collection.Name;
            Name = CollectionMetadata.Observe(Connection, collection).Select(item => item.Name);
            Group = Observable.Return(Optional<CollectionGroupId>.None);
            IsReadOnly = true;
        }

        IsInstalled = Observable.Return(group.HasValue);
        CanToggle = Observable.Return(group.HasValue);
    }

    /// <summary>
    /// The collection as installed in <paramref name="loadout"/>. The revision is the one the group is on now.
    /// <paramref name="parse"/> replaces the reading of the collection.json for a test.
    /// </summary>
    public static NexusCollectionSource Installed(
        IServiceProvider services, LoadoutId loadout, NexusCollectionLoadoutGroup.ReadOnly group, CollectionJsonParser? parse = null) =>
        new(services, loadout, group.Revision, group, parse);

    /// <summary>A revision in the library that is not installed in <paramref name="loadout"/>.</summary>
    public static NexusCollectionSource Pending(IServiceProvider services, LoadoutId loadout, CollectionRevisionMetadata.ReadOnly revision) =>
        new(services, loadout, revision, group: null, parse: null);

    public override IObservable<Optional<CollectionGroupId>> Group { get; }
    public override IObservable<bool> IsInstalled { get; }

    public override PageData InstalledPage(CollectionGroupId group) => new()
    {
        FactoryId = CollectionLoadoutPageFactory.StaticId,
        Context = new CollectionLoadoutPageContext { LoadoutId = _loadout, GroupId = group },
    };

    public override PageData? PendingPage => new()
    {
        FactoryId = CollectionDownloadPageFactory.StaticId,
        Context = new CollectionDownloadPageContext { TargetLoadout = _loadout, CollectionRevisionMetadataId = _revision.CollectionRevisionMetadataId },
    };

    public override string InitialName { get; }
    public override IObservable<string> Name { get; }
    public override string AuthorName { get; }
    public override string VersionText { get; }
    public override IObservable<string> StateLine { get; } = Observable.Return(Language.CollectionLoadoutView_CollectionInstalled);
    public override IObservable<string> Description { get; } = Observable.Return(string.Empty);
    public override CollectionStats Stats { get; }

    public override IObservable<Bitmap?> TileImage => Image(ImagePipelines.GetCollectionTileImagePipeline, _revision.CollectionId);
    public override IObservable<Bitmap?> BackgroundImage => Image(ImagePipelines.GetCollectionBackgroundImagePipeline, _revision.CollectionId);
    public override IObservable<Bitmap?> AuthorAvatar => Image(ImagePipelines.GetUserAvatarPipeline, _revision.Collection.AuthorId);

    public override CollectionBadge? Badge { get; } = new(IconValues.NexusColor, Language.CollectionLoadoutView_NexusMods);
    public override bool IsReadOnly { get; }
    public override IObservable<bool> CanToggle { get; }

    public override IObservable<CollectionInstructions> Instructions => _instructions.AsObservable();

    public override IPageViewModelInterface? CreateDownloads()
    {
        // The collection's archive can leave the library while the collection stays installed; the page has nothing to list then.
        if (HasCollectionLibraryFile() is false) return null;

        return new CollectionDownloadViewModel(
            windowManager: Services.GetRequiredService<IWindowManager>(),
            serviceProvider: Services,
            revisionMetadata: _revision,
            targetLoadout: _loadout,
            isEmbedded: true);
    }

    public override CollectionLink? Link => _link.Value;

    public override IReadOnlyList<IContextMenuItem> MenuItems(ICollectionPageHost host, IContextMenuItem? viewDownloads)
    {
        // A revision that is not installed has no page of its own, so nothing to put in its menu.
        if (_group is not { } group) return [];

        List<IContextMenuItem> items = [CollectionMenuItems.MakeEditableCopy(Services, group.AsCollectionGroup(), host)];
        if (viewDownloads is not null) items.Add(viewDownloads);
        items.Add(Reinstall(group, host));
        return items;
    }

    public override CollectionRemoval InstalledRemoval(CollectionGroupId group) => CollectionRemoval.Confirmed(
        Language.CollectionLoadoutView_UninstallCollection,
        IconValues.PlaylistRemove,
        // Not red: it can be undone. Deleting the revision is the dangerous one.
        ContextMenuItemStyling.Default,
        Observable.Return(true),
        () => NameOf(group),
        DeleteGroup(group),
        toolTip: Language.CollectionLoadoutView_UninstallCollection_ToolTip);

    public override CollectionRemoval? PendingRemoval => CollectionRemoval.Confirmed(
        Language.CollectionDownloadView_Menu_DeleteCollectionRevision,
        IconValues.DeleteOutline,
        ContextMenuItemStyling.Critical,
        Observable.Return(true),
        () => CollectionRevisionMetadata.Load(Connection.Db, _revision.Id).Collection.Name,
        DeleteRevision);

    public override IDisposable Activate()
    {
        // Read here rather than from the embedded download page: that page only activates once its tab is shown, so the
        // instructions tab would stay hidden until someone opened Downloads.
        if (HasCollectionLibraryFile() is false) return Disposable.Empty;

        var file = Services.GetRequiredService<CollectionDownloader>().GetLibraryFile(_revision);
        var parse = _parse ?? Services.GetRequiredService<NexusModsLibrary>().ParseCollectionJsonFile;
        var logger = Services.GetRequiredService<ILogger<NexusCollectionSource>>();

        return Observable
            .FromAsync(cancellationToken => parse(file, cancellationToken).AsTask())
            .SubscribeOn(TaskPoolScheduler.Default)
            .Subscribe(
                root => _instructions.OnNext(CollectionInstructions.From(root, Services)),
                error => logger.LogError(error, "Could not read the instructions of collection {Collection}", _revision.Collection.Name));
    }

    private IObservable<Bitmap?> Image(Func<IServiceProvider, IResourceLoader<EntityId, Bitmap>> pipeline, EntityId entity)
    {
        // Deferred so building a source for a menu entry loads nothing.
        return Observable.Defer(() => ImagePipelines
            .CreateObservable(entity, pipeline(Services))
            .AsSystemObservable()
            .Select(static image => (Bitmap?)image));
    }

    private bool HasCollectionLibraryFile() => Connection.Db.Datoms(
        (NexusModsCollectionLibraryFile.CollectionSlug, _revision.Collection.Slug),
        (NexusModsCollectionLibraryFile.CollectionRevisionNumber, _revision.RevisionNumber)
    ).Count > 0;

    private CollectionLink CreateLink()
    {
        var command = ReactiveCommand.Create(() =>
        {
            var collection = _revision.Collection;
            var gameDomain = Services.GetRequiredService<IGameDomainToGameIdMappingCache>()[collection.GameId];
            Services.GetRequiredService<IOSInterop>().OpenUri(CollectionPageTabs.NexusModsUri(gameDomain, collection.Slug, _revision.RevisionNumber));
        });
        return new CollectionLink(Language.CollectionLoadoutView_OpenOnNexusMods, IconValues.NexusColor, command, Observable.Return(true));
    }

    private async Task DeleteRevision(IWorkspaceController controller)
    {
        var notifications = Services.GetRequiredService<IWindowNotificationService>();
        var revision = _revision.CollectionRevisionMetadataId;
        try
        {
            await Services.GetRequiredService<CollectionDownloader>().DeleteRevision(revision);
            notifications.ShowToast(Language.ToastNotification_Collection_removed, ToastNotificationVariant.Success);

            // Navigate away from the deleted revision's page to the default new tab.
            await Dispatcher.UIThread.InvokeAsync(() =>
                controller.ReplacePages<CollectionDownloadPageContext>(context => context.CollectionRevisionMetadataId == revision));
        }
        catch (Exception ex)
        {
            notifications.ShowToast($"Failed to delete collection: {MarkdownText.Message(ex)}", ToastNotificationVariant.Failure);
        }
    }

    private IContextMenuItem Reinstall(NexusCollectionLoadoutGroup.ReadOnly group, ICollectionPageHost host)
    {
        var loadout = _loadout;
        var notifications = Services.GetRequiredService<IWindowNotificationService>();
        var groupName = group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().Name;

        // Reinstalling while an install of this collection runs would remove the group under it. The running state
        // starts out false so the item is usable before the job monitor says anything.
        var canReinstall = CollectionJobs.ObserveBusy(Services.GetRequiredService<IJobMonitor>(), group.Collection.Id, loadout)
            .Select(isInstalling => isInstalling is false && IsReadOnly)
            .ObserveOn(RxApp.MainThreadScheduler)
            .StartWith(IsReadOnly)
            .DistinctUntilChanged();

        return new ContextMenuItem
        {
            Header = Language.CollectionLoadoutView_ReinstallCollection,
            Icon = IconValues.Refresh,
            ToolTip = Language.CollectionLoadoutView_ReinstallCollection_ToolTip,
            Command = ReactiveCommand.CreateFromTask(async () =>
            {
                var dialog = DialogFactory.CreateStandardDialog(
                    Language.CollectionLoadoutView_ReinstallCollection_Confirmation_Title,
                    new StandardDialogParameters
                    {
                        Text = string.Format(Language.CollectionLoadoutView_ReinstallCollection_Confirmation_Message, groupName),
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
                var result = await host.WindowManager.ShowDialog(dialog, DialogWindowType.Modal);
                if (result.ButtonId != ButtonDefinitionId.Accept) return;

                // The page closes partway through, so every outcome is reported as a toast rather than left to the command.
                try
                {
                    var job = await CollectionReinstaller.Reinstall(
                        Services,
                        group,
                        // The download page shows the install progress, so the open tabs move there before the group goes away.
                        beforeRemove: async () => await CollectionDeleteHelpers.ReplaceInstalledPagesWithDownloadPageAsync(group, host.WorkspaceController)
                    );

                    var newGroup = await job;
                    if (CollectionDownloader.IsFullyInstalled(job.JobDefinition.Items, newGroup.AsCollectionGroup(), Connection.Db))
                        notifications.ShowToast(Language.ToastNotification_Collection_installed, ToastNotificationVariant.Success);
                }
                catch (OperationCanceledException)
                {
                    // Cancelled from the download page, which shows it.
                }
                catch (Exception e)
                {
                    notifications.ShowToast(
                        string.Format(Language.CollectionLoadoutView_ReinstallCollection_Failed, e.Message),
                        ToastNotificationVariant.Failure
                    );
                }
            }, canReinstall),
        };
    }
}
