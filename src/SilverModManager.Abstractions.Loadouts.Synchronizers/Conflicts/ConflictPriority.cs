using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.ValueSerializers;
using TransparentValueObjects;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;

/// <summary>
/// Conflict priority is defined as higher number = winning and lower number = losing.
/// </summary>
[PublicAPI]
[ValueObject<ulong>]
public readonly partial struct ConflictPriority
{
    public static readonly ConflictPriority MaxValue = From(ulong.MaxValue);
    public static readonly ConflictPriority MinValue = From(ulong.MinValue);
}

public class ConflictPriorityAttribute : ScalarAttribute<ConflictPriority, ulong, UInt64Serializer>
{
    public ConflictPriorityAttribute(string ns, string name) : base(ns, name) { }

    protected override ulong ToLowLevel(ConflictPriority value) => value.Value;

    protected override ConflictPriority FromLowLevel(ulong value, AttributeResolver resolver) => ConflictPriority.From(value);
}
