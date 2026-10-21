
namespace NexusMods.Games.FromSoft.EldenRing;

public class EldenRingSynchronizer : AFromSoftSynchronizer
{
    protected internal EldenRingSynchronizer(IServiceProvider provider, IFromSoftGame game)
        : base(provider, game, game.Loader.IntrinsicFiles()) { }
}
