using System.Buffers.Binary;
using FluentAssertions;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;

namespace SilverModManager.Games.FromSoft.Tests.Formats;

/// <summary>
/// Decodes a Kraken member out of a real mod archive. Never part of the default run: it needs a checkout of the
/// erm tool and the mod archive it pinned, and no mod or game file is checked in.
/// </summary>
public class KrakenRealArchiveTests
{
    private const string ModId = "clevers-moveset";
    private const string Member = "chr/c0000.behbnd.dcx";

    private const int DcxHeaderSize = 0x4C;

    [Fact]
    public void AKrakenMemberOfTheMovesetPackageDecodesToABinder()
    {
        var dcx = ErmVendorArchive.ReadPinnedMember(ModId, Member);

        Assert.SkipUnless(dcx.AsSpan(0, 4).SequenceEqual("DCX\0"u8), "the member is not a DCX container");
        Assert.SkipUnless(dcx.AsSpan(0x28, 4).SequenceEqual("KRAK"u8), "the member is not Kraken-compressed");

        var uncompressed = (int)BinaryPrimitives.ReadUInt32BigEndian(dcx.AsSpan(0x1C));
        var compressed = (int)BinaryPrimitives.ReadUInt32BigEndian(dcx.AsSpan(0x20));

        var payload = Kraken.Decompress(dcx.AsSpan(DcxHeaderSize, compressed), uncompressed);

        payload.Should().HaveCount(uncompressed);
        payload.AsSpan(0, 4).SequenceEqual("BND4"u8).Should().BeTrue("a behaviour binder starts with its magic");
    }
}
