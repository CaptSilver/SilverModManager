using R3;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.App.UI.Resources;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.UI.Sdk;

namespace SilverModManager.App.UI.Pages.CollectionDownload;

using CollectionDownloadEntity = SilverModManager.Abstractions.NexusModsLibrary.Models.CollectionDownload;

/// <summary>
/// One mod in the collection page's review list: what is wrong with it and what the player can do about it.
/// </summary>
public sealed class PendingReviewRow : ReactiveR3Object
{
    /// <summary>
    /// The entry for a pending row of the collection.
    /// </summary>
    public PendingReviewRow(NexusCollectionPendingInstall.ReadOnly row, CollectionDownloadEntity.ReadOnly download)
        : this(row.Id, download, download.Name, row.Kind, row.Reason.HasValue ? row.Reason.Value : string.Empty, download.IsOptional)
    {
    }

    /// <summary>
    /// An entry with no database behind it, for the design view.
    /// </summary>
    internal PendingReviewRow(string name, ReviewKind kind, string reason, bool isOptional)
        : this(default, default, name, kind, reason, isOptional)
    {
    }

    private PendingReviewRow(EntityId rowId, CollectionDownloadEntity.ReadOnly download, string name, ReviewKind kind, string reason, bool isOptional)
    {
        RowId = rowId;
        Download = download;
        Name = name;
        Kind = kind;
        Headline = kind switch
        {
            ReviewKind.NoInstaller => Language.CollectionReview_NoInstaller,
            ReviewKind.NeedsChoices => Language.CollectionReview_NeedsChoices,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown review kind"),
        };
        Detail = reason;
        IsOptional = isOptional;
    }

    /// <summary>The pending row this entry stands for; Dismiss removes it from the list by this id.</summary>
    public EntityId RowId { get; }

    /// <summary>The collection download to install.</summary>
    public CollectionDownloadEntity.ReadOnly Download { get; }

    /// <summary>The mod's name in the collection.</summary>
    public string Name { get; }

    /// <summary>Which kind of installer Review needs to open.</summary>
    public ReviewKind Kind { get; }

    /// <summary>One fixed line per kind, so the list reads the same for every game.</summary>
    public string Headline { get; }

    /// <summary>The installer's own explanation, shown muted. Plain text: it can hold braces and is never a format string.</summary>
    public string Detail { get; }

    /// <summary>Only optional mods can be dismissed; a required one stays until it is installed.</summary>
    public bool IsOptional { get; }

    /// <summary>
    /// The archive is no longer in the library, so Review has nothing to install. The view model keeps this up to date
    /// from the download's status.
    /// </summary>
    public BindableReactiveProperty<bool> IsArchiveMissing { get; } = new(value: false);
}
