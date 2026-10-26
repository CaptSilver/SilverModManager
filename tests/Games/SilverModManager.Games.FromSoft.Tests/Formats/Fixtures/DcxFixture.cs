using System.Buffers.Binary;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Formats;

namespace SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;

/// <summary>DCX containers <see cref="Dcx.Write"/> refuses to produce, assembled by hand.</summary>
public static class DcxFixture
{
    /// <summary>A container with the given method and body, laid out the way <see cref="Dcx.Write"/> lays out a zlib one.</summary>
    public static byte[] Container(string method, int uncompressedSize, byte[] body)
    {
        var header = Dcx.Write([], EldenRingGame.Definition.Formats.PackedWrite)[..Dcx.HeaderSize];
        var blob = new byte[Dcx.HeaderSize + body.Length];
        header.CopyTo(blob, 0);
        System.Text.Encoding.ASCII.GetBytes(method).CopyTo(blob, 0x28);
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(0x1C), (uint)uncompressedSize);
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(0x20), (uint)body.Length);
        body.CopyTo(blob, Dcx.HeaderSize);
        return blob;
    }
}
