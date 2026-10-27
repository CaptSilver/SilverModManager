using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.Abstractions.Games.LoadOrder;

/// <summary>Writes a <see cref="ModOrder"/> into a loadout.</summary>
public interface IModOrderSink
{
    /// <summary>Applies the order inside <paramref name="tx"/>; returns the drops the sink itself produced (rule overrides, items it could not seed).</summary>
    IReadOnlyList<ModOrderDrop> Apply(Loadout.ReadOnly loadout, ModOrder order, ModOrderSource source, ITransaction tx);

    /// <summary>The page a dropped-item card sends the user to when the drop names none: where the game's mod order is edited.</summary>
    string FixPage(IGame game);
}
