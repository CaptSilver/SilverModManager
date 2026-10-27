using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games.Merge;

/// <summary>The <see cref="IMergeReports"/> every game shares: it asks the engine that serves the loadout's game.</summary>
public sealed class EngineMergeReports(MergeEngines engines, IConnection connection) : IMergeReports
{
    public Task<MergeReport> ForLoadout(LoadoutId id, CancellationToken token)
        => Task.Run(() =>
        {
            var loadout = Loadout.Load(connection.Db, id);
            var game = loadout.InstallationInstance.GetGame();
            var engine = engines.For(game) ?? throw new InvalidOperationException($"{game.DisplayName} has no merge engine");
            return engine.Report(loadout, MergeSourceMode.Report);
        }, token);
}
