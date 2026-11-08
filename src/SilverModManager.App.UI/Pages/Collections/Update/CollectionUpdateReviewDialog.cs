using ReactiveUI;
using ReactiveUI.SourceGenerators;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Resources;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;

namespace SilverModManager.App.UI.Pages.Collections.Update;

/// <summary>One list of the review: a title with its count, and the lines under it.</summary>
public sealed record ReviewSection(string Title, IReadOnlyList<string> Items);

public interface ICollectionUpdateReviewViewModel : IViewModelInterface
{
    string? Note { get; }
    string? DiskNote { get; }
    string? RestorePointText { get; }
    IReadOnlyList<ReviewSection> Sections { get; }
    IReadOnlyList<string> Warnings { get; }
    bool CanSkipMissing { get; }
    bool SkipMissing { get; set; }
}

/// <summary>
/// The counts-first review of an update: each non-empty list is a collapsed row with its count, so a long change
/// reads as a short page until the user opens one.
/// </summary>
public sealed partial class CollectionUpdateReviewViewModel : AViewModel<ICollectionUpdateReviewViewModel>, ICollectionUpdateReviewViewModel
{
    public CollectionUpdateReviewViewModel(ICollectionUpdate update, CollectionUpdateReview review)
    {
        Note = update.Note;
        DiskNote = DiskLine(review);
        RestorePointText = review.WritesRestorePoint ? Language.CollectionUpdate_Review_RestorePoint : null;
        CanSkipMissing = review.Downloads.Any(static archive => archive.NeedsBrowser);
        Warnings = review.Warnings;
        Sections = SectionsOf(review);
    }

    public string? Note { get; }
    public string? DiskNote { get; }
    public string? RestorePointText { get; }
    public IReadOnlyList<ReviewSection> Sections { get; }
    public IReadOnlyList<string> Warnings { get; }
    public bool CanSkipMissing { get; }

    /// <summary>Leave out the files whose archive is missing instead of stopping.</summary>
    [Reactive] public partial bool SkipMissing { get; set; }

    /// <summary>
    /// The disk line: how far the store can grow, then the source's own sentence about free space. Shown whether or not
    /// anything downloads, because the new list file sits next to the old one until the update finishes either way.
    /// </summary>
    private static string? DiskLine(CollectionUpdateReview review)
    {
        var parts = new List<string>();
        if (review.NewBytesUpperBound is { } bound) parts.Add(string.Format(Language.CollectionUpdate_Review_StoreGrows, bound));
        if (!string.IsNullOrWhiteSpace(review.DiskNote)) parts.Add(review.DiskNote);
        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    private static IReadOnlyList<ReviewSection> SectionsOf(CollectionUpdateReview review)
    {
        var sections = new List<ReviewSection>();

        void Add(string title, IReadOnlyList<string> items, string? detail = null)
        {
            if (items.Count > 0) sections.Add(new ReviewSection($"{title} ({items.Count}{detail})", items));
        }

        Add(Language.CollectionUpdate_Review_Downloads, review.Downloads.Select(static archive =>
            archive.NeedsBrowser ? $"{archive.Name} ({archive.Size}, {archive.Source}, {Language.CollectionUpdate_Review_NeedsBrowser})" : $"{archive.Name} ({archive.Size}, {archive.Source})").ToArray(),
            $", {review.DownloadSize}");
        Add(Language.CollectionUpdate_Review_Added, review.Added);
        Add(Language.CollectionUpdate_Review_Changed, review.Changed);
        Add(Language.CollectionUpdate_Review_Renamed, review.Renamed.Select(static rename => $"{rename.From} → {rename.To}").ToArray());
        Add(review.DroppedAreRemovedNotAsked ? Language.CollectionUpdate_Review_Removed : Language.CollectionUpdate_Review_Dropped, review.Dropped);
        Add(Language.CollectionUpdate_Review_ModOrder, review.ModOrderChanges);
        Add(Language.CollectionUpdate_Review_Plugins, review.PluginChanges);
        Add(Language.CollectionUpdate_Review_UserEditsKept, review.UserEditsKept);
        Add(Language.CollectionUpdate_Review_GameFilesKept, review.GameFilesKept);
        return sections;
    }
}

/// <summary>Builds the dialog that shows an update's review and asks whether to go ahead.</summary>
public static class CollectionUpdateReviewDialog
{
    public static (IDialog Dialog, CollectionUpdateReviewViewModel Model) Create(ICollectionUpdate update, CollectionUpdateReview review, bool isDownloaded)
    {
        var model = new CollectionUpdateReviewViewModel(update, review);
        var dialog = DialogFactory.CreateDialog(
            string.Format(Language.CollectionUpdate_Review_Title, update.Name, update.CurrentVersionText, update.NewVersionText),
            [
                DialogStandardButtons.Cancel,
                new DialogButtonDefinition(
                    isDownloaded ? Language.CollectionUpdate_Review_Update : Language.CollectionUpdate_Review_DownloadAndUpdate,
                    ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Primary),
            ],
            model,
            DialogWindowSize.Large
        );
        return (dialog, model);
    }
}
