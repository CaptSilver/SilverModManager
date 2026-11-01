using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Media.Imaging;
using DynamicData.Kernel;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Helpers;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.App.UI.Pages.Collections;

/// <summary>
/// A collection the user made. It has no author, no numbers and nothing to download; its page is the loadout page
/// scoped to the group.
/// </summary>
public sealed class LocalCollectionSource : CollectionSource
{
    private readonly CollectionGroup.ReadOnly _group;
    private readonly LoadoutId _loadout;

    public LocalCollectionSource(IServiceProvider services, LoadoutId loadout, CollectionGroup.ReadOnly group) : base(services, loadout)
    {
        _group = group;
        _loadout = loadout;
        InitialName = group.AsLoadoutItemGroup().AsLoadoutItem().Name;
        IsReadOnly = group.IsReadOnly;
        Group = Observable.Return(Optional<CollectionGroupId>.Create(group.CollectionGroupId));
    }

    public override IObservable<Optional<CollectionGroupId>> Group { get; }

    public override IObservable<bool> IsInstalled { get; } = Observable.Return(true);

    public override PageData InstalledPage(CollectionGroupId groupId) => new()
    {
        FactoryId = LoadoutPageFactory.StaticId,
        Context = new LoadoutPageContext { LoadoutId = _loadout, GroupScope = groupId },
    };

    public override PageData? PendingPage => null;

    public override string InitialName { get; }

    public override IObservable<string> Name => LoadoutItem.Observe(Connection, _group.CollectionGroupId).Select(item => item.Name);

    public override string AuthorName => string.Empty;
    public override string VersionText => string.Empty;
    public override IObservable<string> StateLine { get; } = Observable.Return(Language.CollectionLoadoutView_CollectionInstalled);
    public override IObservable<string> Description { get; } = Observable.Return(string.Empty);
    public override CollectionStats Stats => CollectionStats.None;
    public override IObservable<Bitmap?> TileImage { get; } = Observable.Return<Bitmap?>(null);
    public override IObservable<Bitmap?> BackgroundImage { get; } = Observable.Return<Bitmap?>(null);
    public override IObservable<Bitmap?> AuthorAvatar { get; } = Observable.Return<Bitmap?>(null);
    public override CollectionBadge? Badge => null;
    public override bool IsReadOnly { get; }
    public override IObservable<bool> CanToggle { get; } = Observable.Return(true);
    public override IObservable<CollectionInstructions> Instructions { get; } = Observable.Return(CollectionInstructions.None);

    public override IPageViewModelInterface? CreateDownloads() => null;

    public override CollectionLink? Link => null;

    public override IReadOnlyList<IContextMenuItem> MenuItems(ICollectionPageHost host, IContextMenuItem? viewDownloads) =>
        [CollectionMenuItems.MakeEditableCopy(Services, _group, host)];

    public override CollectionRemoval InstalledRemoval(CollectionGroupId groupId) => CollectionRemoval.Confirmed(
        Language.Loadout_DeleteCollection_Menu_Text,
        IconValues.DeleteOutline,
        ContextMenuItemStyling.Critical,
        CollectionDeleteHelpers.ObserveCanDeleteCollection(groupId, Connection),
        () => NameOf(groupId),
        DeleteGroup(groupId));

    public override CollectionRemoval? PendingRemoval => null;

    public override IDisposable Activate() => Disposable.Empty;
}
