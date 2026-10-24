using Avalonia.Media.Imaging;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.App.UI.WorkspaceSystem;
using NexusMods.Paths;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.App.UI.Pages.LoadoutPage;

public interface ICollectionLoadoutViewModel : IPageViewModelInterface
{
    LoadoutTreeDataGridAdapter Adapter { get; }

    /// <summary>
    /// Gets whether the collection is local or remote.
    /// </summary>
    bool IsLocalCollection { get; }

    /// <summary>
    /// Gets whether the collection is read-only.
    /// </summary>
    bool IsReadOnly { get; }

    /// <summary>
    /// Gets whether the collection is enabled.
    /// </summary>
    bool IsCollectionEnabled { get; }
    
    /// <summary>
    /// Gets the number of mods installed in the collection, both required and optional.
    /// </summary>
    int InstalledModsCount { get; }

    string Name { get; }
    
    /// <inheritdoc cref="CollectionMetadata.Endorsements"/>
    ulong EndorsementCount { get; }

    /// <inheritdoc cref="CollectionMetadata.TotalDownloads"/>
    ulong TotalDownloads { get; }

    /// <inheritdoc cref="CollectionRevisionMetadata.TotalSize"/>
    Size TotalSize { get; }

    /// <inheritdoc cref="CollectionRevisionMetadata.OverallRating"/>
    Percent OverallRating { get; }
    
    

    RevisionNumber RevisionNumber { get; }

    string AuthorName { get; }

    Bitmap? AuthorAvatar { get; }

    Bitmap? TileImage { get; }

    Bitmap? BackgroundImage { get; }

    R3.ReactiveCommand<R3.Unit> CommandToggle { get; }
    
    R3.ReactiveCommand<R3.Unit> CommandDeleteCollection { get; }
    
    R3.ReactiveCommand<R3.Unit> CommandMakeLocalEditableCopy { get; }

    /// <summary>
    /// Removes the collection's mods and installs them again from the library. Only runs for Nexus collections.
    /// </summary>
    R3.ReactiveCommand<R3.Unit> CommandReinstallCollection { get; }
    
    /// <summary>
    /// Opens the collection's page on Nexus Mods. Only runs for Nexus collections.
    /// </summary>
    R3.ReactiveCommand<R3.Unit> CommandOpenOnNexusMods { get; }

    /// <summary>
    /// Switches to the download tabs.
    /// </summary>
    R3.ReactiveCommand<R3.Unit> CommandShowDownloads { get; }

    /// <summary>
    /// The tab being shown.
    /// </summary>
    CollectionPageTab SelectedTab { get; set; }

    /// <summary>
    /// The collection's download page, shown as the Downloads tab. Null for a local collection, or when the collection's archive is no longer in the library.
    /// </summary>
    ICollectionDownloadViewModel? Downloads { get; }
}
