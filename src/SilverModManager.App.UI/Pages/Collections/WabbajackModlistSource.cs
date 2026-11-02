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
using SilverModManager.App.UI.Pages.LibraryPage.Collections;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Icons;
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

    private static string StateText(WabbajackModlistInstall install) => install.State switch
    {
        WabbajackModlistCardState.Installed => Language.CollectionLoadoutView_CollectionInstalled,
        WabbajackModlistCardState.Incomplete => "Install not finished",
        _ => "Not installed yet",
    };

    public override IObservable<Optional<CollectionGroupId>> Group { get; }
    public override IObservable<bool> IsInstalled { get; }

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

    // The Downloads tab's model follows the run, so the header shows what the tab shows. It reads the model on
    // subscription because the model is built after the source.
    public override IObservable<CollectionInstallStatus?> InstallProgress => Observable.Defer(() =>
    {
        var downloads = _downloads
            ?? throw new InvalidOperationException("The install is followed by the Downloads tab's model, which CreateDownloads builds first");
        return downloads.WhenAnyValue(
            model => model.IsInstalling, model => model.ProgressText, model => model.ProgressFraction, model => model.InstallEtaText,
            (installing, text, fraction, eta) => installing ? new CollectionInstallStatus(text, fraction, eta) : null);
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
        // Last, so it sits next to the page's Remove item.
        items.Add(ReinstallItem(host, model));
        return items;
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

        // Only an installed list has anything to reinstall, and a running install owns the group's contents.
        var canReinstall = Group.Select(static group => group.HasValue)
            .CombineLatest(model.WhenAnyValue(vm => vm.IsInstalling), static (installed, installing) => installed && !installing)
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
