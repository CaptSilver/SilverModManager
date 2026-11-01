using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
}
