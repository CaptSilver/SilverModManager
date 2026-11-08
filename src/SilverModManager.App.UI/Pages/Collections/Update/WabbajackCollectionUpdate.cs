using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Pages.WabbajackGallery;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.App.UI.Resources;
using SilverModManager.DataModel.Undo;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.BuiltInEntities;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Settings;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Gallery;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Models;
using SilverModManager.Wabbajack.Update;

namespace SilverModManager.App.UI.Pages.Collections.Update;

/// <summary>
/// An installed Wabbajack list moving to a newer gallery release, behind the shared update flow. The work stays in
/// <see cref="ModlistUpdater"/> (review, download, apply, undo); this maps it onto the neutral review and outcome the
/// flow shows. The new list file is fetched when the review asks for it, because the review reads it: only the
/// archives are the download step.
/// </summary>
public sealed class WabbajackCollectionUpdate : ICollectionUpdate
{
    private readonly IServiceProvider _services;
    private readonly IConnection _connection;
    private readonly ModlistUpdater _updater;
    private readonly IWabbajackGalleryService _gallery;
    private readonly EntityId _groupId;
    private readonly GalleryEntry? _entry;
    private readonly LibraryFileId? _chosenFile;
    private LibraryFileId? _newFile;
    private IReadOnlySet<RelativePath>? _gameFilesKept;
    private IReadOnlyList<string> _removed = [];

    public WabbajackCollectionUpdate(IServiceProvider services, WabbajackModlistLoadoutGroup.ReadOnly group, GalleryEntry entry)
        : this(services, group, entry.Version, ModlistIdentity.IsReupload(entry, group.Modlist) ? Language.CollectionUpdate_Note_Reupload : null)
    {
        _entry = entry;
    }

    private WabbajackCollectionUpdate(IServiceProvider services, WabbajackModlistLoadoutGroup.ReadOnly group, string version, string? reuploadNote)
    {
        _services = services;
        _connection = services.GetRequiredService<IConnection>();
        _updater = services.GetRequiredService<ModlistUpdater>();
        _gallery = services.GetRequiredService<IWabbajackGalleryService>();
        _groupId = group.Id;

        var installed = group.Modlist;
        Name = installed.Name;
        CurrentVersionText = VersionText(installed.Version);
        NewVersionText = VersionText(version);
        Note = reuploadNote;
    }

    /// <summary>
    /// The update of <paramref name="group"/> to a release that is already in the library, chosen by the user by opening
    /// its page. The gallery is not asked for anything.
    /// </summary>
    public WabbajackCollectionUpdate(IServiceProvider services, WabbajackModlistLoadoutGroup.ReadOnly group, WabbajackModlistLibraryFile.ReadOnly file)
        : this(services, group, version: file.Version, reuploadNote: null)
    {
        _chosenFile = LibraryFileId.From(file.Id);
    }

    /// <summary>How a list's version is named wherever the user sees one.</summary>
    public static string VersionText(string version) => $"Version {version}";

    public string Name { get; }
    public string CurrentVersionText { get; }
    public string NewVersionText { get; }
    public string? Note { get; }

    /// <summary>The installed group as the database holds it now; an update by another route may have moved it since the offer was made.</summary>
    private WabbajackModlistLoadoutGroup.ReadOnly Group() => WabbajackModlistLoadoutGroup.Load(_connection.Db, _groupId);

    private LoadoutId LoadoutOf() => Group().AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().LoadoutId;

    /// <summary>The new release's library file: the one the library already holds, or the gallery's download of it.</summary>
    private async Task<WabbajackModlistLibraryFile.ReadOnly> NewFile(IProgress<string>? progress, CancellationToken ct)
    {
        if (_newFile is { } known && WabbajackModlistLibraryFile.Load(_connection.Db, known.Value) is { } cached && cached.IsValid()) return cached;
        if (_chosenFile is { } chosen)
        {
            _newFile = chosen;
            return WabbajackModlistLibraryFile.Load(_connection.Db, chosen.Value);
        }

        var entry = _entry ?? throw new InvalidOperationException("An update has either a gallery entry or a library file to move to");
        var file = _gallery.FindInLibrary(entry);
        if (file is null)
        {
            progress?.Report(Language.CollectionUpdate_Progress_DownloadingList);
            file = await _gallery.Download(entry, ct);
        }

        _newFile = file;
        var loaded = WabbajackModlistLibraryFile.Load(_connection.Db, file.Value.Value);

        // A file found in the library by its hash may have come from disk and carry no gallery identity. Without it the
        // page of the new file cannot tell it belongs to the same list, and an undo would leave that page offering a
        // second install.
        if (!WabbajackModlistLibraryFile.MachineUrl.TryGetValue(loaded, out _))
        {
            await _services.GetRequiredService<ModlistIdentityBackfill>().Assign(loaded, entry);
            loaded = WabbajackModlistLibraryFile.Load(_connection.Db, file.Value.Value);
        }
        return loaded;
    }

    public async Task<CollectionUpdateReview> Review(IProgress<string>? progress, CancellationToken ct)
    {
        var file = await NewFile(progress, ct);
        var preview = await _updater.Review(Group(), file, ct);
        _gameFilesKept = preview.GameFilesKept.ToHashSet();
        _removed = preview.Removed;
        return ReviewOf(preview, _services.GetRequiredService<ISettingsManager>().Get<DownloadSettings>().MaxParallelDownloads);
    }

    /// <summary>
    /// The preview as the neutral review. The preview's plugin and mod-order conflicts are the user's own choices that
    /// the list disagrees with and that stay as they are, so they join the kept INI tweaks under "Your edits the list
    /// overrides"; a file that an earlier install kept and this one tries again is listed with the changed folders.
    /// </summary>
    public static CollectionUpdateReview ReviewOf(ModlistUpdatePreviewResult preview, int maxParallelDownloads) => new(
        Downloads: [.. preview.Downloads.Select(ArchiveOf)],
        DownloadSize: preview.DownloadSize,
        NewBytesUpperBound: preview.NewBytesUpperBound,
        DiskNote: string.Format(Language.CollectionUpdate_Review_DiskNote_Modlist, maxParallelDownloads),
        Added: preview.Added,
        Changed: [.. preview.Changed, .. preview.RetriedTextures.Select(path => string.Format(Language.CollectionUpdate_Review_Retried, path))],
        Renamed: [.. preview.Renamed.Select(static rename => new ReviewRename(rename.From, rename.To))],
        Dropped: preview.Removed,
        ModOrderChanges: preview.ModOrder,
        PluginChanges: [],
        UserEditsKept: [.. preview.Conflicts, .. preview.Plugins, .. preview.UserTweaksKept.Select(static tweak => tweak.ToString())],
        GameFilesKept: [.. preview.GameFilesKept.Select(static path => path.ToString())],
        Warnings: preview.Warnings,
        WritesRestorePoint: true,
        DroppedAreRemovedNotAsked: true);

    private static ReviewArchive ArchiveOf(ArchivePlanRow row) => new(
        row.Archive.Name,
        Size.FromLong(row.Archive.Size),
        WabbajackArchiveRow.SourceOf(row),
        NeedsBrowser: row.State is ArchiveState.Browser or ArchiveState.SignIn or ArchiveState.Manual);

    public async Task<bool> IsDownloaded(CancellationToken ct)
        => await _updater.IsDownloaded(await NewFile(null, ct), LoadoutOf(), ct);

    public async Task<CollectionUpdateDownload> Download(CancellationToken ct)
    {
        var file = await NewFile(null, ct);
        var loadout = LoadoutOf();
        await _updater.Download(file, loadout, ct);
        return await _updater.IsDownloaded(file, loadout, ct) ? CollectionUpdateDownload.Downloaded : CollectionUpdateDownload.NotAllDownloaded;
    }

    public async Task<CollectionUpdateOutcome> Apply(bool skipMissing, CancellationToken ct)
    {
        try
        {
            var file = await NewFile(null, ct);
            var keep = _gameFilesKept ?? (await _updater.Review(Group(), file, ct)).GameFilesKept.ToHashSet();
            var group = await _updater.Apply(Group(), file, skipMissing, keep, ct);
            return Applied(group);
        }
        catch (Exception e) when (!TimeoutDetection.IsRealCancellation(e))
        {
            return Failed(e);
        }
    }

    private CollectionUpdateOutcome Applied(WabbajackModlistLoadoutGroup.ReadOnly group)
    {
        var skipped = WabbajackSkippedFile.FindByGroup(_connection.Db, group.Id).Count(static file => file.Kind != SkipKind.KeptOriginal);
        var restorePoint = _updater.RestorePointOf(group);
        return new CollectionUpdateOutcome(
            Updated: true,
            Failed: 0,
            Pending: 0,
            DroppedToAsk: [],
            Removed: _removed,
            RestorePoint: restorePoint,
            Undo: UndoOf(restorePoint),
            Error: null,
            ToastText: skipped == 0
                ? string.Format(Language.CollectionUpdate_Updated, MarkdownText.Inline(Name), MarkdownText.InlineMidLine(NewVersionText))
                : string.Format(Language.CollectionUpdate_UpdatedSkipping, MarkdownText.Inline(Name), MarkdownText.InlineMidLine(NewVersionText), skipped),
            Warnings: skipped);
    }

    /// <summary>
    /// A refusal is made before anything is written, so there is nothing to undo even when an earlier, unfinished
    /// update left a restore point. Any other failure stopped the install after the switch: the restore point is the
    /// way back.
    /// </summary>
    private CollectionUpdateOutcome Failed(Exception error)
    {
        var restorePoint = error is ModlistUpdateRefusedException ? null : _updater.RestorePointOf(Group());
        return new CollectionUpdateOutcome(
            Updated: false,
            Failed: 0,
            Pending: 0,
            DroppedToAsk: [],
            Removed: [],
            RestorePoint: restorePoint,
            Undo: UndoOf(restorePoint),
            Error: error,
            ToastText: string.Format(Language.CollectionUpdate_Failed, MarkdownText.InlineMidLine(Name), MarkdownText.InlineMidLine(NewVersionText), MarkdownText.Message(error)));
    }

    private Func<CancellationToken, Task>? UndoOf(TxId? restorePoint)
        => restorePoint is null ? null : ct => Undo(_services, Group(), ct);

    /// <summary>
    /// Reverts <paramref name="group"/> to the restore point its update wrote. Returns the version text of the list the
    /// group is back on, read after the revert: the group read before it still names the version that was undone.
    /// </summary>
    public static async Task<string> Undo(IServiceProvider services, WabbajackModlistLoadoutGroup.ReadOnly group, CancellationToken ct)
    {
        var connection = services.GetRequiredService<IConnection>();
        var undo = services.GetRequiredService<UndoService>();
        var loadout = group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().LoadoutId;
        await services.GetRequiredService<ModlistUpdater>().Undo(group, tx =>
        {
            var timestamp = Transaction.Load(connection.Db, EntityId.From(tx.Value)).Timestamp;
            return undo.RevertTo(new LoadoutRevision(loadout.Value, EntityId.From(tx.Value), timestamp));
        }, ct);

        return VersionText(WabbajackModlistLoadoutGroup.Load(connection.Db, group.Id).Modlist.Version);
    }
}
