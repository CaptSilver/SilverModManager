using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Media.Imaging;
using DynamicData.Kernel;
using ReactiveUI;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.App.UI.Pages.Collections;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// A <see cref="CollectionSource"/> the test drives by hand: every observable the page reads is a subject, so a fact can
/// make the group appear, vanish or emit from a pool thread, and count how often the page called the expensive members.
/// </summary>
internal sealed class TestCollectionSource : CollectionSource
{
    public TestCollectionSource(IServiceProvider services, LoadoutId? loadout = null) : base(services, loadout) { }

    public BehaviorSubject<Optional<CollectionGroupId>> GroupSubject { get; } = new(Optional<CollectionGroupId>.None);
    public BehaviorSubject<bool> InstalledSubject { get; } = new(true);
    public BehaviorSubject<string> NameSubject { get; } = new("Test collection");
    public BehaviorSubject<string> StateLineSubject { get; } = new("Test state");
    public BehaviorSubject<string> DescriptionSubject { get; } = new("");
    public BehaviorSubject<bool> CanToggleSubject { get; } = new(false);

    /// <summary>What <see cref="CreateDownloads"/> returns; null means the collection has nothing to download.</summary>
    public IPageViewModelInterface? DownloadsToReturn { get; set; }

    public int CreateDownloadsCalls { get; private set; }
    public int ActivateCalls { get; private set; }
    public int DisposedActivations { get; private set; }
    public int RemoveCalls { get; private set; }

    /// <summary>The <c>viewDownloads</c> argument the page passed to <see cref="MenuItems"/>.</summary>
    public IContextMenuItem? ViewDownloadsSeen { get; private set; }
    public bool MenuItemsCalled { get; private set; }

    public override IObservable<Optional<CollectionGroupId>> Group => GroupSubject;
    public override IObservable<bool> IsInstalled => InstalledSubject;
    public override PageData InstalledPage(CollectionGroupId group) => throw new NotSupportedException();
    public override PageData? PendingPage => null;
    public override string InitialName => "Test collection";
    public override IObservable<string> Name => NameSubject;
    public override string AuthorName => "Tester";
    public override string VersionText => "Version 9";
    public override IObservable<string> StateLine => StateLineSubject;
    public override IObservable<string> Description => DescriptionSubject;
    public override CollectionStats Stats => CollectionStats.None;
    public override IObservable<Bitmap?> TileImage { get; } = Observable.Return<Bitmap?>(null);
    public override IObservable<Bitmap?> BackgroundImage { get; } = Observable.Return<Bitmap?>(null);
    public override IObservable<Bitmap?> AuthorAvatar { get; } = Observable.Return<Bitmap?>(null);
    public override CollectionBadge? Badge => null;
    public override bool IsReadOnly => false;
    public override IObservable<bool> CanToggle => CanToggleSubject;
    public override IObservable<CollectionInstructions> Instructions { get; } = Observable.Return(CollectionInstructions.None);

    public override IPageViewModelInterface? CreateDownloads()
    {
        CreateDownloadsCalls++;
        return DownloadsToReturn;
    }

    public override CollectionLink? Link => null;

    public override IReadOnlyList<IContextMenuItem> MenuItems(ICollectionPageHost host, IContextMenuItem? viewDownloads)
    {
        MenuItemsCalled = true;
        ViewDownloadsSeen = viewDownloads;
        var own = new ContextMenuItem
        {
            Header = "Source item",
            Icon = IconValues.Copy,
            Command = ReactiveCommand.Create(() => { }),
        };
        return viewDownloads is null ? [own] : [own, viewDownloads];
    }

    public override CollectionRemoval InstalledRemoval(CollectionGroupId group) => new()
    {
        Header = "Remove test collection",
        Icon = IconValues.PlaylistRemove,
        Styling = ContextMenuItemStyling.Default,
        ToolTip = "Removes the test collection",
        CanExecute = Observable.Return(true),
        Remove = _ =>
        {
            RemoveCalls++;
            return Task.CompletedTask;
        },
        ConfirmAndRemove = (_, _) => Task.CompletedTask,
    };

    public override CollectionRemoval? PendingRemoval => null;

    public override IDisposable Activate()
    {
        ActivateCalls++;
        return Disposable.Create(() => DisposedActivations++);
    }
}
