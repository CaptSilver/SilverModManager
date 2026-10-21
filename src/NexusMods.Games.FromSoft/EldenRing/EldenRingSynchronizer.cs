using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Games.FromSoft.Me3;

namespace NexusMods.Games.FromSoft.EldenRing;

public class EldenRingSynchronizer : AFromSoftSynchronizer
{
    protected internal EldenRingSynchronizer(IServiceProvider provider, IFromSoftGame game)
        : base(provider, game, [new Me3ProfileFile(provider.GetRequiredService<ILogger<Me3ProfileFile>>())]) { }
}
