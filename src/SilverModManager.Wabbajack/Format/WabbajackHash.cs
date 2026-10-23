using System.Buffers.Binary;
using NexusMods.Hashing.xxHash3;

namespace SilverModManager.Wabbajack.Format;

/// <summary>
/// Wabbajack writes an xxHash64 as base64 of its eight little-endian bytes. The app's <see cref="Hash"/>
/// carries the same 64 bits, so this is a plain conversion.
/// </summary>
public static class WabbajackHash
{
    private const int Length = sizeof(ulong);

    /// <exception cref="FormatException">The input is not base64 of exactly eight bytes.</exception>
    public static Hash Parse(string base64)
    {
        Span<byte> bytes = stackalloc byte[Length + 4];
        if (!Convert.TryFromBase64String(base64, bytes, out var written) || written != Length)
            throw new FormatException($"'{base64}' is not the base64 of an eight byte hash");
        return Hash.From(BinaryPrimitives.ReadUInt64LittleEndian(bytes));
    }

    public static string ToBase64(Hash hash)
    {
        Span<byte> bytes = stackalloc byte[Length];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, hash.Value);
        return Convert.ToBase64String(bytes);
    }
}
