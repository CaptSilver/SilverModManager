using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.FromSoft.EldenRing;

/// <summary>
/// Keeps the <c>Game</c> folder out of every code path that writes: mods live in a package folder beside the app's
/// data and reach the game through the launcher, never through a deployment.
/// </summary>
public class EldenRingSynchronizer : ALoadoutSynchronizer
{
    protected internal EldenRingSynchronizer(IServiceProvider provider) : base(provider) { }

    protected override IGamePathFilter GameSpecificPathFilter { get; } = GamePathFilters.Create(EldenRingPaths.IsArchiveOrMedia);

    public override bool IsIgnoredBackupPath(GamePath path) => EldenRingPaths.IsInGameFolder(path);

    protected override bool IsNeverWritten(GamePath path) => EldenRingPaths.IsInGameFolder(path);
}
