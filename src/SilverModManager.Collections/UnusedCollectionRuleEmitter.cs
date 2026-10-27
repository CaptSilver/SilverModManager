using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Collections;

/// <summary>
/// One suggestion per collection download that stored ordering rules name but the collection group holds no install of
/// (an optional download the user declined). Computed on every run rather than stored at install, because the collection
/// page installs a single download on its own and nothing would rewrite a stored row then.
/// </summary>
public sealed class UnusedCollectionRuleEmitter : ILoadoutDiagnosticEmitter
{
    /// <inheritdoc />
    [Obsolete("To be replaced with the overload that takes in the sync tree")]
    public IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken) =>
        Diagnose(loadout, FrozenDictionary<GamePath, SyncNode>.Empty, cancellationToken);

    /// <inheritdoc />
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var db = loadout.Db;
        foreach (var item in LoadoutItem.FindByLoadout(db, loadout.LoadoutId))
        {
            var group = NexusCollectionLoadoutGroup.Load(db, item.Id);
            if (!group.IsValid()) continue;

            var revision = group.Revision;
            foreach (var download in revision.Downloads)
            {
                if (!IsNamedByARule(download, db)) continue;
                if (CollectionDownloader.GetStatus(download, group.AsCollectionGroup(), db).IsInstalled(out _)) continue;
                yield return UnusedCollectionRuleDiagnostics.CreateOptionalModRulesNotUsed(download.Name, revision.Collection.Name);
            }
        }
    }

    private static bool IsNamedByARule(CollectionDownload.ReadOnly download, IDb db) =>
        db.Datoms(CollectionDownloadRules.Source, download.Id).Count > 0 || db.Datoms(CollectionDownloadRules.Other, download.Id).Count > 0;
}
