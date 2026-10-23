using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.IndexSegments;

namespace SilverModManager.DataModel.Undo;

/// <summary>
/// The datoms that turn one entity's state into another's. Only the attribute and value are compared,
/// never the transaction a datom was written in, so a value that was rewritten unchanged is not a difference.
/// </summary>
internal static class OwnedEntityDiff
{
    internal readonly record struct Change(AttributeId A, ValueTag Tag, byte[] Value, bool IsRetract);

    /// <summary>
    /// The changes that make <paramref name="current"/> look like <paramref name="wanted"/>.
    /// </summary>
    internal static List<Change> Between(EntitySegment current, EntitySegment wanted)
    {
        var changes = new List<Change>();

        var wantedAttributes = new HashSet<AttributeId>();
        foreach (var av in wanted.GetAVEnumerable())
            wantedAttributes.Add(av.A);

        foreach (var av in current.GetAVEnumerable())
        {
            if (wanted.Contains(av)) continue;

            // The attribute has a different value in the wanted state. Owned attributes hold one value, so
            // asserting the wanted one replaces this one and an explicit retract would be a second write
            // to the same datom.
            if (wantedAttributes.Contains(av.A)) continue;

            changes.Add(new Change(av.A, av.ValueType, av.Value.ToArray(), IsRetract: true));
        }

        foreach (var av in wanted.GetAVEnumerable())
        {
            if (current.Contains(av)) continue;
            changes.Add(new Change(av.A, av.ValueType, av.Value.ToArray(), IsRetract: false));
        }

        return changes;
    }

    /// <summary>
    /// The attributes on which <paramref name="current"/> differs from <paramref name="wanted"/>: a value
    /// either side has that the other lacks.
    /// </summary>
    /// <remarks>
    /// This is deliberately stricter than <see cref="Between"/>. Between leaves out the retract of a value
    /// the wanted state replaces, trusting the assert to drop it; this check is what finds out if it didn't.
    /// </remarks>
    internal static List<AttributeId> Mismatches(EntitySegment current, EntitySegment wanted)
    {
        var mismatches = new List<AttributeId>();
        foreach (var av in current.GetAVEnumerable())
        {
            if (!wanted.Contains(av) && !mismatches.Contains(av.A))
                mismatches.Add(av.A);
        }

        foreach (var av in wanted.GetAVEnumerable())
        {
            if (!current.Contains(av) && !mismatches.Contains(av.A))
                mismatches.Add(av.A);
        }

        return mismatches;
    }
}
