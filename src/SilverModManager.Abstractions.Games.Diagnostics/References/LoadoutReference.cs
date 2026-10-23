using JetBrains.Annotations;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Ids;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Diagnostics.References;

/// <summary>
/// A reference to a <see cref="Loadout"/>.
/// </summary>
[PublicAPI]
public record LoadoutReference : IDataReference<LoadoutId, Loadout.ReadOnly>
{
    /// <inheritdoc/>
    public required TxId TxId { get; init; }

    /// <inheritdoc/>
    public required LoadoutId DataId { get; init; }

    /// <inheritdoc/>
    public Loadout.ReadOnly ResolveData(IServiceProvider serviceProvider, IConnection dataStore)
    {
        var db = dataStore.AsOf(TxId);
        return Loadout.Load(db, DataId.Value);
    }

    /// <inheritdoc/>
    public string ToStringRepresentation(Loadout.ReadOnly data) => data.Name;
}
