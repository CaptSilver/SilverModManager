using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Media.Imaging;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Controls.MarkdownRenderer;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.App.UI.Pages.Collections.Update;
using SilverModManager.App.UI.Pages.LibraryPage.Collections;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Icons;
using SilverModManager.Wabbajack.Gallery;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Models;
using Observable = System.Reactive.Linq.Observable;
using ReactiveCommand = ReactiveUI.ReactiveCommand;

namespace SilverModManager.App.UI.Pages.Collections;

/// <summary>
/// A Wabbajack modlist from the library, installed in a loadout or not. The header reads the library file; the
/// banner and the list's own blurb are read when the page activates, and the Downloads tab is the modlist page.
/// </summary>
public sealed class WabbajackModlistSource : CollectionSource
{
    private static readonly WabbajackModlistInstall NotInstalled = new(WabbajackModlistCardState.NotInstalled, null, false);

    private readonly LibraryFileId _file;
    private readonly IWabbajackModlistService? _service;
    private readonly IObservable<WabbajackModlistInstall> _install;
    private readonly BehaviorSubject<Bitmap?> _banner = new(null);
    private readonly BehaviorSubject<string> _description = new(string.Empty);
    private readonly BehaviorSubject<Uri?> _readme = new(null);
    private readonly BehaviorSubject<CollectionInstructions> _instructions = new(CollectionInstructions.None);
    private readonly Lazy<CollectionLink> _link;

    private IWabbajackModlistViewModel? _downloads;

    /// <param name="loadoutId">The loadout the install is looked for in. Null follows the loadout the Downloads tab's picker holds.</param>
    /// <param name="service">Replaces the real <see cref="IWabbajackModlistService"/> that Activate reads the banner and blurb through.</param>
    public WabbajackModlistSource(
        IServiceProvider services,
        LoadoutId? loadoutId,
        WabbajackModlistLibraryFile.ReadOnly file,
        IWabbajackModlistService? service = null) : base(services, loadoutId)
    {
        _file = LibraryFileId.From(file.Id);
        _service = service;
        _link = new Lazy<CollectionLink>(CreateLink);

        InitialName = file.Name;
        Name = Observable.Return(file.Name);
        AuthorName = file.Author;
        VersionText = $"Version {file.Version}";
        Stats = new CollectionStats(Rating: null, Endorsements: null, Downloads: null, TotalSize: file.ArchiveSize, ArchiveCount: file.ArchiveCount);

        _install = loadoutId is { } loadout ? WabbajackModlistInstallState.Observe(Connection, _file, loadout) : ObservePickedLoadout();

        Group = _install.Select(install => install.Group is { } group ? Optional<CollectionGroupId>.Create(group) : Optional<CollectionGroupId>.None);
        IsInstalled = _install.Select(static install => install.State == WabbajackModlistCardState.Installed);
        StateLine = _install.Select(StateText);
        CanToggle = _install.Select(static install => install.Group is not null && install.Finished);
    }

    // The loadout the Downloads tab's picker holds; "Create new loadout" holds no install.
    private IObservable<WabbajackModlistInstall> ObservePickedLoadout() => Observable.Defer(() =>
    {
        var downloads = _downloads
            ?? throw new InvalidOperationException("The loadout is picked on the Downloads tab, which CreateDownloads builds first");
        return downloads.WhenAnyValue(model => model.SelectedLoadout)
            .Select(choice => choice?.Id is { } picked
                ? WabbajackModlistInstallState.Observe(Connection, _file, picked)
                : Observable.Return(NotInstalled))
            .Switch();
    });

    private static string StateText(WabbajackModlistInstall install) => install is { State: WabbajackModlistCardState.NotInstalled, OtherVersion: { } other }
        ? string.Format(Language.CollectionUpdate_OtherVersionInstalled, other)
        : StateTextOf(install);

    private static string StateTextOf(WabbajackModlistInstall install) => install.State switch
    {
        WabbajackModlistCardState.Installed => Language.CollectionLoadoutView_CollectionInstalled,
        WabbajackModlistCardState.Incomplete => "Install not finished",
        WabbajackModlistCardState.Superseded => string.Format(Language.CollectionUpdate_Superseded, install.SupersededBy),
        _ => "Not installed yet",
    };

    /// <summary>
    /// While another release of this list holds the loadout's install, the update of that install to this very file.
    /// The page offers it where a plain Install would put a second copy of the list beside the first.
    /// </summary>
    public override IObservable<ICollectionUpdate?> UpdateToThisVersion => _install
        .Select(static install => install.OtherGroup)
        .DistinctUntilChanged()
        .Select(ICollectionUpdate? (other) =>
        {
            if (other is not { } groupId) return null;
            var group = WabbajackModlistLoadoutGroup.Load(Connection.Db, groupId);
            var file = WabbajackModlistLibraryFile.Load(Connection.Db, _file.Value);
            return group.IsValid() && file.IsValid() ? new WabbajackCollectionUpdate(Services, group, file) : null;
        });

    public override IObservable<Optional<CollectionGroupId>> Group { get; }
    public override IObservable<bool> IsInstalled { get; }

    /// <summary>
    /// The Undo and Finish items are there while the group records a restore point, and the picker goes once the list has
    /// a gallery identity, so the page asks for the menu again when either changes. The group is part of the answer: a
    /// page that follows the Downloads picker between two loadouts that both have a restore point needs the items of
    /// the other group.
    /// </summary>
    public override IObservable<Unit> MenuChanged => Group
        .Select(group => group.HasValue ? ObserveRestorePoint(group.Value.Value).Select(restorePoint => (Group: (EntityId?)group.Value.Value, RestorePoint: restorePoint)) : Observable.Return((Group: (EntityId?)null, RestorePoint: false)))
        .Switch()
        .CombineLatest(ObserveHasIdentity(), static (state, identity) => (state.Group, state.RestorePoint, Identity: identity))
        .DistinctUntilChanged()
        .Select(static _ => Unit.Default);

    private IObservable<bool> ObserveRestorePoint(EntityId group) => Observable.Defer(() => Connection.Revisions
        .Select(db => WabbajackUpdateMenuItems.HasRestorePoint(db, group))
        .Prepend(WabbajackUpdateMenuItems.HasRestorePoint(Connection.Db, group)));

    private IObservable<bool> ObserveHasIdentity() => Observable.Defer(() => Connection.Revisions
        .Select(db => HasIdentity(db, _file))
        .Prepend(HasIdentity(Connection.Db, _file))
        .DistinctUntilChanged());

    private static bool HasIdentity(IDb db, LibraryFileId file)
        => WabbajackModlistLibraryFile.Load(db, file.Value) is { } list && list.IsValid() && WabbajackModlistLibraryFile.MachineUrl.TryGetValue(list, out _);

    /// <summary>
    /// Once an update has moved this list's install to a newer file, the page of the old file is superseded and the tab
    /// goes to the new file's page; after an undo it goes back the other way. Only a change while the page is open moves
    /// it: the value on subscription is where the user chose to be, such as a newer release opened while an older one is
    /// installed. Only a source fixed to a loadout can tell; a page that follows the Downloads picker never moves.
    /// </summary>
    public override IObservable<PageData> MovedTo => LoadoutId is not { } loadout
        ? Observable.Never<PageData>()
        : Observable.Defer(() => Connection.Revisions
                .Prepend(Connection.Db)
                .Scan((HeldGroup: false, Successor: (LibraryFileId?)null), (previous, db) => (
                    HeldGroup: previous.HeldGroup || WabbajackModlistInstallState.FindGroup(db, _file, loadout) is not null,
                    Successor: WabbajackModlistInstallState.SuccessorOf(db, _file, loadout, previous.HeldGroup))))
            .Select(static state => state.Successor)
            .DistinctUntilChanged()
            .Skip(1)
            .Where(static successor => successor is not null)
            .Select(successor => new PageData
            {
                FactoryId = WabbajackModlistPageFactory.StaticId,
                Context = new WabbajackModlistPageContext { LibraryFileId = successor.GetValueOrDefault(), TargetLoadout = loadout },
            });

    // The page survives an install and a removal, so it is the same page whichever entry opens it.
    public override PageData InstalledPage(CollectionGroupId group) => Page;
    public override PageData? PendingPage => Page;

    private PageData Page => new()
    {
        FactoryId = WabbajackModlistPageFactory.StaticId,
        Context = new WabbajackModlistPageContext { LibraryFileId = _file, TargetLoadout = LoadoutId },
    };

    public override string InitialName { get; }
    public override IObservable<string> Name { get; }
    public override string AuthorName { get; }
    public override string VersionText { get; }
    public override IObservable<string> StateLine { get; }
    public override IObservable<string> Description => _description.AsObservable();
    public override CollectionStats Stats { get; }
    public override IObservable<Bitmap?> TileImage => _banner.AsObservable();
    public override IObservable<Bitmap?> BackgroundImage => _banner.AsObservable();
    public override IObservable<Bitmap?> AuthorAvatar { get; } = Observable.Return<Bitmap?>(null);
    public override CollectionBadge? Badge { get; } = new(IconValues.Download, "Wabbajack");
    public override bool IsReadOnly => false;
    public override IObservable<bool> CanToggle { get; }
    public override IObservable<CollectionInstructions> Instructions => _instructions.AsObservable();

    // The Downloads tab's model reports the step that runs (check, downloads or install), and the header is the only
    // place that shows it. It reads the model on subscription because the model is built after the source.
    public override IObservable<CollectionInstallStatus?> InstallProgress => Observable.Defer(() =>
    {
        var downloads = _downloads
            ?? throw new InvalidOperationException("The install is followed by the Downloads tab's model, which CreateDownloads builds first");
        return downloads.WhenAnyValue(
            model => model.RunningStatus,
            status => status.Text is { Length: > 0 } text ? new CollectionInstallStatus(text, status.Fraction ?? 0, status.IsIndeterminate) : null);
    });

    public override IPageViewModelInterface? CreateDownloads()
    {
        // Kept for the picker and the more menu. The model loads and plans when its tab is first shown, so opening an
        // installed list's page does not start hashing the game folder.
        var model = Services.GetRequiredService<IWabbajackModlistViewModel>();
        model.FileId = _file;
        model.PinnedLoadout = LoadoutId;
        _downloads = model;
        return model;
    }

    public override CollectionLink? Link => _link.Value;

    public override IReadOnlyList<IContextMenuItem> MenuItems(ICollectionPageHost host, IContextMenuItem? viewDownloads)
    {
        var model = _downloads ?? throw new InvalidOperationException("The more menu is built after CreateDownloads");

        List<IContextMenuItem> items = [];
        if (viewDownloads is not null) items.Add(viewDownloads);
        items.Add(Item("Check inbox", IconValues.Download, model.CheckInboxCommand));
        items.Add(Item("Open inbox folder", IconValues.FolderOpen, model.OpenInboxFolderCommand));
        items.Add(Item("Choose download folder...", IconValues.FolderOpen, model.ChooseDownloadsFolderCommand));
        items.Add(Item("Use the default download folder", IconValues.Refresh, model.UseDefaultDownloadsFolderCommand));
        items.AddRange(UpdateItems(host, model));
        // Last, so it sits next to the page's Remove item.
        items.Add(ReinstallItem(host, model));
        return items;
    }

    // Undo and Finish while the group records a restore point; the picker while the list's gallery entry is unknown and
    // more than one entry could be it.
    private IEnumerable<IContextMenuItem> UpdateItems(ICollectionPageHost host, IWabbajackModlistViewModel model)
    {
        var loadout = LoadoutId ?? model.SelectedLoadout?.Id;
        if (loadout is not { } picked) yield break;

        var db = Connection.Db;
        if (WabbajackModlistInstallState.ReadInstall(db, _file, picked).Group is { } group && WabbajackUpdateMenuItems.HasRestorePoint(db, group.Value))
        {
            yield return WabbajackUpdateMenuItems.UndoUpdate(Services, host, group.Value);
            yield return WabbajackUpdateMenuItems.FinishUpdate(Services, host, group.Value);
        }

        var file = WabbajackModlistLibraryFile.Load(db, _file.Value);
        if (file.IsValid()
            && !HasIdentity(db, _file)
            && Services.GetRequiredService<IWabbajackGalleryClient>().GetCached() is { } listing
            && Services.GetRequiredService<ModlistIdentityBackfill>().Candidates(file, listing) is { Count: > 1 } candidates)
            yield return WabbajackUpdateMenuItems.PickGalleryEntry(Services, host, file, candidates);
    }

    private ContextMenuItem ReinstallItem(ICollectionPageHost host, IWabbajackModlistViewModel model)
    {
        var reinstaller = new WabbajackModlistReinstaller(
            Connection,
            Services.GetRequiredService<ILoadoutManager>(),
            Services.GetRequiredService<IErrorPresenter>(),
            Services.GetRequiredService<ModlistInstallRuns>(),
            model,
            _file,
            () => LoadoutId ?? model.SelectedLoadout?.Id);

        // Anything the install put in the loadout can be put in again, an unfinished install included. Only a run that
        // owns the group's contents (an install, or an apply that reads them) holds it back.
        var canReinstall = Group
            .DistinctUntilChanged()
            .Select(group => group.HasValue ? ObserveContentsInUse(model, group.Value).Select(static inUse => !inUse) : Observable.Return(false))
            .Switch()
            .StartWith(false)
            .DistinctUntilChanged()
            .ObserveOn(RxApp.MainThreadScheduler);

        return new ContextMenuItem
        {
            Header = "Reinstall modlist",
            Icon = IconValues.Refresh,
            ToolTip = "Removes what the install put in the loadout and installs it again from the library. Nothing is downloaded.",
            Command = ReactiveCommand.CreateFromTask(() => reinstaller.Reinstall(host), canReinstall),
        };
    }

    private IObservable<bool> ObserveContentsInUse(IWabbajackModlistViewModel model, CollectionGroupId group)
    {
        var loadout = LoadoutItem.Load(Connection.Db, group).LoadoutId;
        var monitor = Services.GetRequiredService<IJobMonitor>();
        var installingHere = Services.GetRequiredService<ModlistInstallRuns>().ObserveInstallingInto(loadout);
        var applying = monitor.HasActiveJob<SynchronizeLoadoutJob>(job => job.LoadoutId == loadout)
            .Prepend(monitor.IsAnyJobActive<SynchronizeLoadoutJob>(job => job.LoadoutId == loadout));
        return model.WhenAnyValue(vm => vm.IsInstalling)
            .CombineLatest(installingHere, applying, static (installing, installingInto, isApplying) => installing || installingInto || isApplying)
            .DistinctUntilChanged();
    }

    private static ContextMenuItem Item(string header, IconValue icon, ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> command) =>
        new() { Header = header, Icon = icon, Command = command };

    // The list stays in the library, and the entry falls back to the pending one, as a Nexus uninstall falls back to the revision.
    public override CollectionRemoval InstalledRemoval(CollectionGroupId group) => CollectionRemoval.Confirmed(
        "Remove modlist",
        IconValues.PlaylistRemove,
        ContextMenuItemStyling.Default,
        Observable.Return(true),
        () => NameOf(group),
        DeleteGroup(group));

    // The left menu's pending entry asks the same plan dialog as the Library card; the page's own item removes at once.
    public override CollectionRemoval? PendingRemoval
    {
        get
        {
            var remover = new WabbajackModlistRemover(Services);
            return new CollectionRemoval
            {
                Header = "Remove modlist from library",
                Icon = IconValues.DeleteOutline,
                Styling = ContextMenuItemStyling.Critical,
                CanExecute = Observable.Return(true),
                Remove = controller => remover.Remove(remover.Plan(_file), deleteArchives: false, controller),
                ConfirmAndRemove = (windowManager, controller) => remover.ConfirmAndRemove(windowManager, controller, _file),
            };
        }
    }

    public override IDisposable Activate()
    {
        var logger = Services.GetRequiredService<ILogger<WabbajackModlistSource>>();
        var service = _service ?? Services.GetRequiredService<IWabbajackModlistService>();

        // The Downloads tab may not be shown yet, and an install that was started before this page opened is going on
        // whichever tab is open.
        // The hold lasts as long as the page is open, not as long as the Downloads tab is shown: the header follows the
        // run on every tab.
        var disposables = new CompositeDisposable();
        if (_downloads is not null) disposables.Add(_downloads.FollowRunningInstall());

        // Open parses the list for its banner and ReadBlurb reuses that parse, so the header costs one parse.
        Observable
            .FromAsync(cancellationToken => ReadHeader(service, logger, cancellationToken))
            .SubscribeOn(TaskPoolScheduler.Default)
            .Subscribe(_ => { }, error => logger.LogError(error, "Could not read the header of modlist {Modlist}", _file))
            .DisposeWith(disposables);
        return disposables;
    }

    private async Task ReadHeader(IWabbajackModlistService service, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            var details = await service.Open(_file, cancellationToken);
            _banner.OnNext(details.Banner);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A list that cannot be read fails again in the Downloads tab's check, with the message the page shows.
            logger.LogDebug(e, "No banner for modlist {Modlist}", _file);
        }

        try
        {
            var blurb = await service.ReadBlurb(_file, cancellationToken);
            _description.OnNext(blurb.Description);
            _readme.OnNext(blurb.Readme);
            _instructions.OnNext(InstructionsOf(blurb));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogDebug(e, "No description for modlist {Modlist}", _file);
        }
    }

    private CollectionInstructions InstructionsOf(WabbajackModlistBlurb blurb)
    {
        var text = string.Join("\n\n", new[] { blurb.Description, blurb.Readme is { } readme ? $"Readme: {readme}" : "" }.Where(part => part != ""));
        if (text == "") return CollectionInstructions.None;

        var renderer = Services.GetRequiredService<IMarkdownRendererViewModel>();
        renderer.Contents = text;
        return new CollectionInstructions(renderer, [], []);
    }

    private CollectionLink CreateLink()
    {
        var command = ReactiveCommand.Create(
            () =>
            {
                if (_readme.Value is { } readme) Services.GetRequiredService<IOSInterop>().OpenUri(readme);
            },
            _readme.Select(static readme => readme is not null).ObserveOn(RxApp.MainThreadScheduler));
        return new CollectionLink("Open readme", IconValues.OpenInNew, command, _readme.Select(static readme => readme is not null));
    }
}
