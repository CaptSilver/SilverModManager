using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.LoadOrderFile;

/// <summary>The synchronizer of every <see cref="LoadOrderFileGame"/>: the defaults, plus the game's generated order file.</summary>
public sealed class LoadOrderFileSynchronizer(IServiceProvider provider, LoadOrderFileGameDefinition definition) : ALoadoutSynchronizer(provider)
{
    private readonly GeneratedOrderFile _orderFile = new(definition, provider.GetRequiredService<ILogger<GeneratedOrderFile>>());

    public override Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout)
        => new() { [_orderFile.Path] = _orderFile };

    /// <summary>
    /// A native Linux build is located like the Windows one (a Steam id has no platform), but lays its files out
    /// differently, so a loadout for it is refused before anything is written.
    /// </summary>
    protected override Task PreflightLoadout(Loadout.ReadOnly loadout)
    {
        var gameFolder = loadout.InstallationInstance.Locations.ToAbsolutePath(new GamePath(LocationId.Game, RelativePath.Empty));
        if (definition.NativeBuildFiles.Any(file => gameFolder.Combine(file).FileExists))
            throw new NativeBuildNotSupportedException(definition.DisplayName);

        return base.PreflightLoadout(loadout);
    }
}
