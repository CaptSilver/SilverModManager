using System.Runtime.InteropServices;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.DatomIterators;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;

namespace SilverModManager.DataModel.SchemaVersions;

/// <summary>
/// The step the migrations that fold duplicate rows into one share: point every reference to a row at the row that
/// survives.
/// </summary>
public static class ReferenceRepointing
{
    /// <summary>
    /// Rewrites each reference to <paramref name="duplicate"/> to name <paramref name="survivor"/>. A reference
    /// for which <paramref name="skip"/> returns true is left as it is, for the caller to handle itself.
    /// </summary>
    public static void Repoint(ITransaction tx, IDb db, EntityId duplicate, EntityId survivor, Func<Datom, bool>? skip = null)
    {
        Span<byte> survivorValue = stackalloc byte[sizeof(ulong)];
        MemoryMarshal.Write(survivorValue, in survivor);

        foreach (var reference in db.ReferencesTo(duplicate))
        {
            if (reference.Prefix.ValueTag != ValueTag.Reference) continue;
            if (skip is not null && skip(reference)) continue;

            if (db.AttributeCache.IsCardinalityMany(reference.A)) tx.Add(reference.Retract());
            tx.Add(reference.E, reference.A, ValueTag.Reference, survivorValue);
        }
    }
}
