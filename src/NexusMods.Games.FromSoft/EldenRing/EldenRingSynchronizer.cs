using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.FromSoft.Me3;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FromSoft.EldenRing;

/// <summary>
/// Keeps the <c>Game</c> folder out of every code path that writes: mods live in a package folder beside the app's
/// data and reach the game through the launcher, never through a deployment.
/// </summary>
public class EldenRingSynchronizer : ALoadoutSynchronizer
{
    private readonly Dictionary<GamePath, IIntrinsicFile> _intrinsicFiles;

    protected internal EldenRingSynchronizer(IServiceProvider provider) : base(provider)
    {
        var profile = new Me3ProfileFile(provider.GetRequiredService<ILogger<Me3ProfileFile>>());
        _intrinsicFiles = new Dictionary<GamePath, IIntrinsicFile> { { profile.Path, profile } };
    }

    public override Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout) => _intrinsicFiles;

    protected override IGamePathFilter GameSpecificPathFilter { get; } = GamePathFilters.Create(EldenRingPaths.IsArchiveOrMedia);

    public override bool IsIgnoredBackupPath(GamePath path) => EldenRingPaths.IsInGameFolder(path);

    protected override bool IsNeverWritten(GamePath path) => EldenRingPaths.IsInGameFolder(path);
}
