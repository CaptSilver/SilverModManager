using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.BuiltInEntities;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts;

/// <summary>
/// Explicit loadout snapshots. These aren't really snapshots in the traditional sense, instead they are simply
/// markers on a transaction that indicate that we want this transaction to show up in the undo history.
/// </summary>
[Include<Transaction>]
public partial class LoadoutSnapshot : IModelDefinition
{
    private const string Namespace = "SilverModManager.Loadouts.LoadoutSnapshot";
    
    /// <summary>
    /// Inserted on a transaction when a loadout is reverted to some previous state.
    /// </summary>
    public static readonly ReferencesAttribute<Loadout> Snapshot = new(Namespace, nameof(Snapshot));
}
