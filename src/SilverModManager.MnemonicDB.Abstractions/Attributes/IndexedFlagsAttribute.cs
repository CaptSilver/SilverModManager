using System;
using SilverModManager.MnemonicDB.Abstractions.BuiltInEntities;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.ValueSerializers;

namespace SilverModManager.MnemonicDB.Abstractions.Attributes;

/// <summary>
/// An attribute that defines the indexing status of an attribute, this is stored as a custom attribute type because
/// older DBs store data in a different format and a conversion on read is required.
/// </summary>
public class IndexedFlagsAttribute : ScalarAttribute<IndexedFlags, byte, UInt8Serializer>
{
    /// <inheritdoc />
    public IndexedFlagsAttribute(string ns, string name) : 
        base(ns, name)
    {
        DefaultValue = IndexedFlags.None;
    }

    /// <summary>
    /// This performs the proper conversion from the low level representation to the high level representation. Taking
    /// into consideration old attribute formats
    /// </summary>
    public override IndexedFlags ReadValue(ReadOnlySpan<byte> span, ValueTag tag, AttributeResolver resolver)
    {
        // Old format, an attribute's existance means it's indexed
        if (tag == ValueTag.Null)
            return IndexedFlags.Indexed;
        if (tag == ValueTag.UInt8)
            return (IndexedFlags)span[0];
        return IndexedFlags.None;
    }

    /// <inheritdoc />
    protected override byte ToLowLevel(IndexedFlags value)
    {
        return (byte)value;
    }

    /// <inheritdoc />
    protected override IndexedFlags FromLowLevel(byte value, AttributeResolver resolver)
    {
        throw new NotSupportedException("This method should never be called.");
    }
}
