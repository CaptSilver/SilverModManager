using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SilverModManager.Games.FromSoft.Tests.Saves;

/// <summary>
/// Builds a well-formed BND4 container, the shape the game's save files have: a 0x40 byte header, one 0x20 byte
/// entry per body, UTF-16 names, and each body stored behind its own MD5. Synthetic saves only; a real save never
/// goes into a fixture.
/// </summary>
public static class Bnd4
{
    public static byte[] Build(IReadOnlyList<byte[]> bodies, IReadOnlySet<int>? brokenDigests = null)
    {
        var count = bodies.Count;
        var namesOffset = 0x40 + count * 0x20;

        var names = new List<byte>();
        var nameOffsets = new int[count];
        for (var i = 0; i < count; i++)
        {
            nameOffsets[i] = namesOffset + names.Count;
            names.AddRange(Encoding.Unicode.GetBytes($"USER_DATA{i:000}"));
            names.AddRange(new byte[2]);
        }

        var dataOffset = namesOffset + names.Count;
        var table = new byte[count * 0x20];
        var blobs = new List<byte>();
        for (var i = 0; i < count; i++)
        {
            var digest = MD5.HashData(bodies[i]);
            if (brokenDigests?.Contains(i) == true) digest = digest.Select(b => (byte)(b ^ 0xFF)).ToArray();

            var blob = digest.Concat(bodies[i]).ToArray();
            var entry = table.AsSpan(i * 0x20, 0x20);
            BinaryPrimitives.WriteInt64LittleEndian(entry[8..], blob.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[16..], (uint)(dataOffset + blobs.Count));
            BinaryPrimitives.WriteUInt32LittleEndian(entry[20..], (uint)nameOffsets[i]);
            blobs.AddRange(blob);
        }

        var header = new byte[0x40];
        "BND4"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)count);
        return [.. header, .. table, .. names, .. blobs];
    }
}
