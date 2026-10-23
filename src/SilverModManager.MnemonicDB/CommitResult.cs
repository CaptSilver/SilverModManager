using System;
using System.Collections.Generic;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.MnemonicDB;

/// <inheritdoc />
public class CommitResult(IDb db, IDictionary<EntityId, EntityId> remaps) : ICommitResult
{
    /// <inheritdoc />
    public EntityId this[EntityId id] =>
        remaps.TryGetValue(id, out var found) ? found : id;

    /// <inheritdoc />
    public TxId NewTx => db.BasisTxId;

    /// <inheritdoc />
    public IDb Db => db;
}
