using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Microsoft.Extensions.DependencyInjection;

namespace SilverModManager.Games.CreationEngine.Emitters;

/// <summary>
/// Says which settings the game changed in an owned INI that the next apply puts back: keys the app manages and
/// keys an enabled mod's tweak sets. Settings nothing else owns are not mentioned, the apply keeps those. Only the
/// loadout that was applied last has a live file to compare, and the notice is gone once the apply has run.
/// </summary>
public class GameIniChangedByGameEmitter(IGameIniFiles game, IServiceProvider provider) : ILoadoutDiagnosticEmitter
{
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (!Loadout.OwnsGameInis.Get(loadout)) yield break;
        if (!GameInstallMetadata.LastSyncedLoadout.TryGetValue(loadout.Installation, out var lastApplied) || lastApplied != loadout.Id) yield break;

        var fileStore = provider.GetRequiredService<IFileStore>();
        var locations = loadout.InstallationInstance.Locations;
        foreach (var path in GameInis.Paths(game))
        {
            if (GameInis.StateOf(loadout, path) is not { } state || !locations.ContainsKey(path.LocationId)) continue;

            var file = locations.ToAbsolutePath(path);
            if (!file.FileExists) continue;
            if (await GameInis.LastWrittenBytes(fileStore, state, deployed: null) is not { } written) continue;

            var changes = await GameInis.ChangesByGame(game, fileStore, loadout, path, await file.ReadAllBytesAsync(cancellationToken), written);
            var replaced = changes.Where(c => c.Kind != GameIniChangeKind.Adopted).Select(Describe).ToList();
            if (replaced.Count > 0)
                yield return Diagnostics.CreateGameIniChangesReplaced(path.FileName.ToString(), string.Join("\n", replaced));
        }
    }

    private static string Describe(GameIniChange change) => change.Kind switch
    {
        GameIniChangeKind.ManagedKeyReset => $"- the game changed {change.Key} in [{change.Section}]; it is reset on the next Apply",
        _ => $"- the game changed {change.Key} in [{change.Section}]; overridden by {change.Owner}",
    };
}
