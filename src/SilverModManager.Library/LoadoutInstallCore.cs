using System.Collections.Concurrent;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Library;

/// <summary>
/// <see cref="ILoadoutInstallCore"/> over the loadout manager.
/// </summary>
internal sealed class LoadoutInstallCore(IConnection connection, ILoadoutManager loadouts, ICollectionOwnership ownership) : ILoadoutInstallCore
{
    // Lazy, so two callers that race on the same key cannot both start the downloads.
    private readonly ConcurrentDictionary<(LoadoutId Loadout, string Key), Lazy<Task<IReadOnlyList<InstallOutcome>>>> _inFlight = new();

    public async Task<IReadOnlyList<InstallOutcome>> Run(LoadoutId loadout, string inFlightKey, IReadOnlyList<ResolvedInstall> items, CancellationToken token)
    {
        var key = (loadout, inFlightKey);
        var run = _inFlight.GetOrAdd(key, _ => new Lazy<Task<IReadOnlyList<InstallOutcome>>>(() => RunOnce(loadout, items, token)));
        try
        {
            return await run.Value;
        }
        finally
        {
            // Only the run that is still registered is removed; a later run under the same key stays.
            _inFlight.TryRemove(new KeyValuePair<(LoadoutId, string), Lazy<Task<IReadOnlyList<InstallOutcome>>>>(key, run));
        }
    }

    private async Task<IReadOnlyList<InstallOutcome>> RunOnce(LoadoutId loadout, IReadOnlyList<ResolvedInstall> items, CancellationToken token)
    {
        // The downloads come first: a restore point written before a download that then fails would leave an undo
        // step that undoes nothing.
        var resolved = new LibraryItem.ReadOnly[items.Count];
        for (var i = 0; i < items.Count; i++)
            resolved[i] = items[i].InLibrary ?? await items[i].Download(token);

        await LoadoutSnapshot.WriteRestorePoint(connection, loadout);

        var outcomes = new List<InstallOutcome>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = resolved[i];
            var outcome = await LoadoutPackages.InstallOrEnable(connection, ownership, item, loadout, async () => await loadouts.InstallItem(item, loadout));
            outcomes.Add(new InstallOutcome(items[i].Name, item, items[i].InLibrary is null, outcome, items[i].Dependency));
        }

        return outcomes;
    }
}
