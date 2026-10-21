using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.CreationEngine;

public class BethesdaSynchronizer(IServiceProvider provider, IBethesdaGame game) : ALoadoutSynchronizer(provider)
{
    // Evaluated on every path so a game's ProtectedFolders can depend on state its constructor sets.
    protected override IGamePathFilter GameSpecificPathFilter { get; } =
        GamePathFilters.Create(path => game.ProtectedFolders.Any(path.InFolder));

    public override Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout) => game.IntrinsicFiles(loadout, provider);

    public override bool IsIgnoredBackupPath(GamePath path)
    {
        // Don't backup BSA files by default
        return path.Extension == KnownCEExtensions.BSA || path.Extension == KnownCEExtensions.BA2;
    }
}
