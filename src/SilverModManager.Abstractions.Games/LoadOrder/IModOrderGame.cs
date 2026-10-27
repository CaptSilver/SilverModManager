namespace SilverModManager.Abstractions.Games.LoadOrder;

/// <summary>A game that supplies its own order sink or Vortex order format. Games without it use <see cref="DefaultPriorityOrderSink"/> and no Vortex key.</summary>
public interface IModOrderGame
{
    IModOrderSink? OrderSink { get; }

    IVortexOrderFormat? VortexOrderFormat { get; }
}
