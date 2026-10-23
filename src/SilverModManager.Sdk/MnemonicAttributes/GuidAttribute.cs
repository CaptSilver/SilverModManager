using System.Diagnostics;
using System.Runtime.InteropServices;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.ValueSerializers;

namespace SilverModManager.Sdk.MnemonicAttributes;

/// <summary>
/// Attribute for <see cref="Guid"/>.
/// </summary>
public sealed class GuidAttribute(string ns, string name) : ScalarAttribute<Guid, UInt128, UInt128Serializer>(ns, name)
{
    /// <inheritdoc />
    protected override UInt128 ToLowLevel(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        var success = value.TryWriteBytes(bytes);
        Debug.Assert(success);

        return MemoryMarshal.Read<UInt128>(bytes);
    }

    /// <inheritdoc />
    protected override Guid FromLowLevel(UInt128 value, AttributeResolver resolver)
    {
        Span<byte> bytes = stackalloc byte[16];
        MemoryMarshal.Write(bytes, value);

        return new Guid(bytes);
    }
}
