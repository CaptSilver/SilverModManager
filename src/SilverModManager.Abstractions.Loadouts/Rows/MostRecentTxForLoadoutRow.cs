using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.Abstractions.Loadouts.Rows;

public readonly partial record struct MostRecentTxForLoadoutRow(EntityId LoadoutId, EntityId TxId, int ItemCount);
