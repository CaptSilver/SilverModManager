using System.Reactive;
using Avalonia.Media.Imaging;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using ReactiveUI;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.UI.Sdk;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Helpers;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Jobs;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.App.UI.Pages.Collections;

/// <summary>
/// One collection in one loadout, whatever it came from: everything the collection page and its menu entry show
/// or do. The page, the installed entry and the pending entry each build their own instance. Construction is
/// cheap: it reads the database and builds no view model. Only the page calls <see cref="CreateDownloads"/> and
/// <see cref="Activate"/>; a menu entry never does, because the menu builds one source per collection of the
/// loadout's game.
/// </summary>
/// <remarks>
/// Every observable a source exposes may emit off the UI thread. Sources never marshal; the page moves what it binds
/// to the UI thread.
/// </remarks>
public abstract class CollectionSource
{
    protected CollectionSource(IServiceProvider services, LoadoutId? loadoutId)
    {
        Services = services;
        Connection = services.GetRequiredService<IConnection>();
        LoadoutId = loadoutId;
    }

    protected IServiceProvider Services { get; }
    protected IConnection Connection { get; }

    /// <summary>The group's name as it is now, for the confirmation a removal asks.</summary>
    protected string NameOf(CollectionGroupId group) => LoadoutItem.Load(Connection.Db, group).Name;

    /// <summary>Removes the group from its loadout, after pointing the tabs that show it somewhere that still exists.</summary>
    protected Func<IWorkspaceController, Task> DeleteGroup(CollectionGroupId group) => controller =>
        CollectionDeleteHelpers.DeleteCollectionAsync(
            group,
            Services.GetRequiredService<ILoadoutManager>(),
            controller,
            Connection,
            Services.GetRequiredService<IWindowNotificationService>());

    /// <summary>
    /// The loadout. Null only for a Wabbajack list opened from the Library card's Open, the gallery or a drop,
    /// where the Downloads tab's loadout picker chooses it.
    /// </summary>
    public LoadoutId? LoadoutId { get; }

    /// <summary>The installed group, when there is one. Follows installs and removals.</summary>
    public abstract IObservable<Optional<CollectionGroupId>> Group { get; }

    /// <summary>
    /// Decides the page's starting tab and nothing else: an unfinished install opens on Downloads. Nexus and local
    /// return true, their page only opens for an installed group; Wabbajack follows its install state.
    /// </summary>
    public abstract IObservable<bool> IsInstalled { get; }

    /// <summary>The page an installed entry, a mod row's "open collection" and the delete helper open.</summary>
    public abstract PageData InstalledPage(CollectionGroupId group);

    /// <summary>The page a pending entry opens. Null for a source that is never pending (local collections).</summary>
    public abstract PageData? PendingPage { get; }

    /// <summary>The name as it is now, read at construction: the menu entries' initial text.</summary>
    public abstract string InitialName { get; }
    public abstract IObservable<string> Name { get; }
    public abstract string AuthorName { get; }

    /// <summary>"Revision 193", "Version 3.2.1" or "".</summary>
    public abstract string VersionText { get; }

    /// <summary>The header's first line.</summary>
    public abstract IObservable<string> StateLine { get; }

    /// <summary>The header's description block; "" hides it.</summary>
    public abstract IObservable<string> Description { get; }

    /// <summary>A null field hides its panel.</summary>
    public abstract CollectionStats Stats { get; }
    public abstract IObservable<Bitmap?> TileImage { get; }
    public abstract IObservable<Bitmap?> BackgroundImage { get; }
    public abstract IObservable<Bitmap?> AuthorAvatar { get; }

    /// <summary>The logo and name of where the collection comes from; null for a local collection.</summary>
    public abstract CollectionBadge? Badge { get; }
    public abstract bool IsReadOnly { get; }

    /// <summary>Whether the header's enable toggle is shown: a group exists that may be switched on.</summary>
    public abstract IObservable<bool> CanToggle { get; }

    public abstract IObservable<CollectionInstructions> Instructions { get; }

    /// <summary>
    /// How far an install of this collection has got, for the header to show on every tab, or null while none is running.
    /// A source that has no install of its own to report leaves it at null.
    /// </summary>
    public virtual IObservable<CollectionInstallStatus?> InstallProgress => System.Reactive.Linq.Observable.Return<CollectionInstallStatus?>(null);

    /// <summary>
    /// Builds the Downloads tab's view model, embedded (no header of its own). Null when there is nothing to
    /// download from. The page calls it once in its constructor; the view host activates it only when the tab is shown.
    /// Never called for a menu entry.
    /// </summary>
    public abstract IPageViewModelInterface? CreateDownloads();

    /// <summary>"Open on Nexus Mods", "Open readme" or null.</summary>
    public abstract CollectionLink? Link { get; }

    /// <summary>
    /// The page's "more" menu in its order, without the removal item (the page appends that). <paramref name="viewDownloads"/>
    /// is the page's own "View downloads" item, null when there is no Downloads tab; the source places it, so Nexus keeps
    /// its order. Called after <see cref="CreateDownloads"/>.
    /// </summary>
    public abstract IReadOnlyList<IContextMenuItem> MenuItems(ICollectionPageHost host, IContextMenuItem? viewDownloads);

    public abstract CollectionRemoval InstalledRemoval(CollectionGroupId group);
    public abstract CollectionRemoval? PendingRemoval { get; }

    /// <summary>
    /// Starts the reads the header needs that are not instant (instructions, a list's banner and blurb).
    /// Called by the page when it activates; disposing the result stops them.
    /// </summary>
    public abstract IDisposable Activate();
}

/// <summary>The numbers in a collection page's header. A null field hides its panel.</summary>
public sealed record CollectionStats(Percent? Rating, ulong? Endorsements, ulong? Downloads, Size? TotalSize, int? ArchiveCount)
{
    public static CollectionStats None { get; } = new(null, null, null, null, null);
}

/// <summary>An install in flight, as the header shows it. <paramref name="EtaText"/> is empty until there is enough to estimate from.</summary>
public sealed record CollectionInstallStatus(string Text, double Fraction, string EtaText);

public sealed record CollectionBadge(IconValue Icon, string Text);

public sealed record CollectionLink(string Text, IconValue Icon, ReactiveCommand<Unit, Unit> Command, IObservable<bool> IsVisible);
