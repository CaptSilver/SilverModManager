using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel;

/// <summary>
/// Moves the game INIs of a Wabbajack import made before imports went through the INI bake. Such an import holds
/// each profile INI as a file at the INI's real path in the game settings group, which would be a second writer
/// next to the bake. The pass makes the file's bytes the loadout's baseline for that path and deletes the file.
/// </summary>
/// <remarks>
/// A startup pass rather than a schema migration: a migration sees no game, so it cannot tell which paths a game
/// treats as INIs, and the state rows it would add live in a project the migrations do not reference. The bytes
/// are already in the file store under the file's hash, so only the hash moves. A loadout that already has a
/// baseline for the path (the bake took the file from disk first) keeps it. Files of any other group are left alone.
/// </remarks>
public sealed class ImportedIniFilesPass(IConnection connection, ILogger<ImportedIniFilesPass> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Run();
        }
        catch (Exception e)
        {
            // Not worth failing startup over: the pass runs again on the next start.
            logger.LogError(e, "Could not move the INI files of imported modlists into their loadouts' baselines");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Moves every such file in one transaction and returns how many files left their real path.</summary>
    public async Task<int> Run()
    {
        var db = connection.Db;
        using var tx = connection.BeginTransaction();
        var moved = 0;

        foreach (var loadout in Loadout.All(db))
        {
            // By the game id alone: locating the install would throw for a library that is not mounted this start.
            if (!loadout.IsValid() || !loadout.TryGetGame(out var found) || found is not IGameIniFiles game) continue;

            var iniPaths = GameInis.Paths(game).ToHashSet();
            var known = LoadoutIniState.FindByLoadout(db, loadout.LoadoutId).Select(s => (GamePath)s.Path).ToHashSet();
            foreach (var file in LoadoutItem.FindByLoadout(db, loadout.LoadoutId).OfTypeLoadoutItemWithTargetPath().OfTypeLoadoutFile())
            {
                var path = (GamePath)file.AsLoadoutItemWithTargetPath().TargetPath;
                if (!iniPaths.Contains(path) || !InGameSettingsGroup(file.AsLoadoutItemWithTargetPath().AsLoadoutItem())) continue;

                if (known.Add(path))
                {
                    _ = new LoadoutIniState.New(tx)
                    {
                        LoadoutId = loadout.LoadoutId,
                        Path = path.ToGamePathParentTuple(loadout.LoadoutId),
                        Baseline = file.Hash,
                    };
                }

                logger.LogInformation("Moving the imported {Path} of loadout {Loadout} into its INI baselines", path, loadout.Name);
                tx.Delete(file.Id, recursive: false);
                moved++;
            }
        }

        if (moved > 0) await tx.Commit();
        return moved;
    }

    private static bool InGameSettingsGroup(LoadoutItem.ReadOnly item) =>
        item.GetThisAndParents().Any(i => i.Contains(LoadoutItemGroup.GameSettingsGroup));
}
