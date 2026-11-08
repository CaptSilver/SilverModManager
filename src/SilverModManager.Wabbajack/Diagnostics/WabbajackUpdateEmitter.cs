using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Gallery;

namespace SilverModManager.Wabbajack.Diagnostics;

/// <summary>
/// Suggests, once per installed modlist, that the gallery has a newer release. It reads only the cached listing: a
/// diagnostics run must not wait on the network. <see cref="GalleryRefreshDiagnosticsRunner"/> reruns it after a fetch.
/// </summary>
public sealed class WabbajackUpdateEmitter(ModlistUpdateCheck check) : ILoadoutDiagnosticEmitter
{
    public IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
        => DiagnoseAsync(loadout, cancellationToken);

    private async IAsyncEnumerable<Diagnostic> DiagnoseAsync(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        foreach (var update in check.FromCache(loadout.Db, loadout.LoadoutId))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = update.Group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem();
            var newVersion = update.IsReupload ? $"{update.Newer.Version} (re-uploaded by the author)" : update.Newer.Version;
            var download = update.Newer.ArchiveCount is { } count
                ? $", {count} archives (list total), {update.DownloadSize}"
                : "";
            var listing = update.IsStale
                ? $"This comes from an offline copy of the gallery listing fetched {update.ListingFetchedAt:u}."
                : "";

            yield return WabbajackDiagnostics.CreateModlistUpdateAvailable(
                new LoadoutItemGroupReference { TxId = loadout.Db.BasisTxId, DataId = update.Group.Id },
                item.Name,
                update.Installed.Version,
                newVersion,
                download,
                listing) with
            {
                Actions = [DiagnosticActions.OpenWabbajackModlist(update.Installed.Id, loadout.Id)],
            };
        }
    }
}
