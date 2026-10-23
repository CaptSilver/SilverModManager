using System.Collections.Generic;
using SilverModManager.MnemonicDB.Abstractions.DatomIterators;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;

namespace SilverModManager.MnemonicDB;

/// <summary>
/// A comparer that compares only the Attribute and Value of a Datom used for unique constraints.
/// </summary>
public class UniqueAttributeEqualityComparer : IComparer<Datom>
{
    /// <summary>
    /// The global instance of the <see cref="UniqueAttributeEqualityComparer"/>.
    /// </summary>
    public static UniqueAttributeEqualityComparer Instance { get; } = new();


    /// <inheritdoc />
    public int Compare(Datom a, Datom b)
    {
        var cmp = AComparer.Compare(a, b);
        if (cmp != 0)
            return cmp;
        
        return ValueComparer.Compare(a, b);
    }
}
