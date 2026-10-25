using System.Globalization;
using NexusMods.Paths;
using ReactiveUI;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages.Thunderstore;
using SilverModManager.Networking.NexusWebApi;

namespace SilverModManager.App.UI.Pages.NexusCatalog;

/// <summary>
/// One collection of the catalog as the Nexus Mods page lists it. It is a reactive object, not a record, so a
/// collection entering the library flips its card's button in place instead of rebuilding the list, which would send
/// the grid back to the top.
/// </summary>
public sealed class NexusCollectionRow(NexusCollectionSearchHit hit, CollectionRevisionMetadataId? libraryRevision = null) : ReactiveObject
{
    private CollectionRevisionMetadataId? _libraryRevision = libraryRevision;

    public NexusCollectionSearchHit Hit { get; } = hit;
    public string Name => Hit.Name;
    public string Summary => Hit.Summary;
    public bool IsAdult => Hit.AdultContent;
    public Uri? ImageUrl => Hit.ImageUrl;

    /// <summary>"by someone", or nothing when the collection credits no one.</summary>
    public string AuthorLine => Hit.Author == "" ? "" : $"by {Hit.Author}";

    /// <summary>"45 mods  ·  118 MB  ·  1.2K endorsements  ·  1.5M downloads", the small print under the title.</summary>
    public string Details => string.Create(CultureInfo.InvariantCulture,
        $"{Hit.ModCount} mods  ·  {SizeComponent._FormatValue(Size.From(Hit.TotalSizeBytes))}  ·  {ThunderstoreRowText.CompactCount(Hit.Endorsements)} endorsements  ·  {ThunderstoreRowText.CompactCount(Hit.Downloads)} downloads");

    /// <summary>The revision of this collection the library holds, if it holds one; the newest when it holds several.</summary>
    public CollectionRevisionMetadataId? LibraryRevision
    {
        get => _libraryRevision;
        set
        {
            if (_libraryRevision.Equals(value)) return;
            this.RaiseAndSetIfChanged(ref _libraryRevision, value);
            this.RaisePropertyChanged(nameof(PrimaryButtonText));
        }
    }

    /// <summary>"Open" for a collection the library already holds, "Install" otherwise.</summary>
    public string PrimaryButtonText => _libraryRevision.HasValue ? "Open" : "Install";
}
