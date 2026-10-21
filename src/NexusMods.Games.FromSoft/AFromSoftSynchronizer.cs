using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FromSoft;

/// <summary>
/// Keeps the game's subfolder out of every code path that writes: mods live in a package folder beside the app's
/// data and reach the game through the launcher, never through a deployment.
/// </summary>
public abstract class AFromSoftSynchronizer : ALoadoutSynchronizer
{
    private readonly FromSoftGameLayout _layout;
    private readonly Dictionary<GamePath, IIntrinsicFile> _intrinsicFiles;

    protected AFromSoftSynchronizer(IServiceProvider provider, IFromSoftGame game, IEnumerable<IIntrinsicFile> intrinsicFiles) : base(provider)
    {
        _layout = game.Layout;
        _intrinsicFiles = intrinsicFiles.ToDictionary(file => file.Path);
        GameSpecificPathFilter = GamePathFilters.Create(path => FromSoftPaths.IsArchiveOrMedia(_layout, path));
    }

    public override Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout) => _intrinsicFiles;

    protected override IGamePathFilter GameSpecificPathFilter { get; }

    public override bool IsIgnoredBackupPath(GamePath path) => FromSoftPaths.IsInGameFolder(_layout, path);

    protected override bool IsNeverWritten(GamePath path) => FromSoftPaths.IsInGameFolder(_layout, path);
}
