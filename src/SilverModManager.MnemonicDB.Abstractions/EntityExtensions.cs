using System;
using System.Linq;
using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.BuiltInEntities;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.MnemonicDB.Abstractions;

/// <summary>
/// Extension methods for entities.
/// </summary>
[PublicAPI]
public static class EntityExtensions
{
    /// <summary>
    /// Gets the timestamp of the transaction that created the model.
    /// </summary>
    public static DateTimeOffset GetCreatedAt<T>(this T model, DateTimeOffset defaultValue = default)
        where T : IReadOnlyModel<T>
    {
        if (model.Count == 0) return defaultValue;
        var minTx = model.Min(m => m.T);

        var tx = new Transaction.ReadOnly(model.Db, EntityId.From(minTx.Value));
        return Transaction.Timestamp.Get(tx);
    }
}
