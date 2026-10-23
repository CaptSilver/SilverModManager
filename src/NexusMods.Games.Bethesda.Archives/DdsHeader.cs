// Follows Wabbajack.Compression.BSA/DirectXTexUtil.cs.
// ------------------------------------------------------------------------
// DirectXTex Utility - A simple class for generating DDS Headers
// Copyright(c) 2018 Philip/Scobalula
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
// ------------------------------------------------------------------------
using System.Buffers.Binary;
using System.Text;

namespace NexusMods.Games.Bethesda.Archives;

/// <summary>
/// Builds the DDS header that goes in front of a BA2 texture's mip data. The BA2 keeps no header, and an
/// extracted texture is hashed and compared against what the modlist recorded, so the bytes follow the
/// header rules Wabbajack writes (the DirectXTex ones): the legacy pixel format when DDS has one for the
/// DXGI format and a DX10 extension header otherwise.
/// </summary>
internal static class DdsHeader
{
    private const uint HeaderSize = 124;
    private const uint PixelFormatSize = 32;

    private const uint FlagCaps = 0x1, FlagHeight = 0x2, FlagWidth = 0x4, FlagPitch = 0x8, FlagPixelFormat = 0x1000;
    private const uint FlagMipCount = 0x20000, FlagLinearSize = 0x80000;

    private const uint PfAlphaPixels = 0x1, PfAlpha = 0x2, PfFourCc = 0x4, PfRgb = 0x40, PfLuminance = 0x20000, PfBump = 0x80000;

    // DDS_SURFACE_FLAGS_MIPMAP: the MIPMAP caps bit together with COMPLEX.
    private const uint CapsTexture = 0x1000, CapsMipmap = 0x400008;

    private const uint Dx10Texture2D = 3, Dx10MiscCube = 0x4;

    private readonly record struct PixelFormat(uint Flags, uint FourCc, uint Bits, uint R, uint G, uint B, uint A);

    private static uint FourCc(string text) => BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes(text));

    private static PixelFormat Legacy(uint fourCc) => new(PfFourCc, fourCc, 0, 0, 0, 0, 0);

    public static byte[] Build(int width, int height, int mipLevels, uint dxgiFormat, bool isCubeMap)
    {
        var pixelFormat = LegacyPixelFormat(dxgiFormat);
        var (rowPitch, slicePitch, compressed) = Pitch(dxgiFormat, (uint)width, (uint)height);

        var flags = FlagCaps | FlagHeight | FlagWidth | FlagPixelFormat;
        var caps = CapsTexture;
        if (mipLevels > 0) flags |= FlagMipCount;
        if (mipLevels > 1) caps |= CapsMipmap;
        flags |= compressed ? FlagLinearSize : FlagPitch;

        var dx10 = pixelFormat is null;
        var buffer = new byte[4 + HeaderSize + (dx10 ? 20 : 0)];
        "DDS "u8.CopyTo(buffer);

        void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), value);

        Put(4, HeaderSize);
        Put(8, flags);
        Put(12, (uint)height);
        Put(16, (uint)width);
        Put(20, compressed ? (uint)slicePitch : (uint)rowPitch);
        Put(24, 1); // depth
        Put(28, (uint)mipLevels);
        // Eleven reserved words, then the pixel format.
        var pf = pixelFormat ?? Legacy(FourCc("DX10"));
        Put(76, PixelFormatSize);
        Put(80, pf.Flags);
        Put(84, pf.FourCc);
        Put(88, pf.Bits);
        Put(92, pf.R);
        Put(96, pf.G);
        Put(100, pf.B);
        Put(104, pf.A);
        Put(108, caps);

        if (dx10)
        {
            Put(128, dxgiFormat);
            Put(132, Dx10Texture2D);
            Put(136, isCubeMap ? Dx10MiscCube : 0);
            Put(140, 1); // array size: a cube map's six faces count as one
            Put(144, 0);
        }

        return buffer;
    }

    private static PixelFormat? LegacyPixelFormat(uint format) => format switch
    {
        28 => new(PfRgb | PfAlphaPixels, 0, 32, 0xff, 0xff00, 0xff0000, 0xff000000),
        35 => new(PfRgb, 0, 32, 0xffff, 0xffff0000, 0, 0),
        49 => new(PfLuminance | PfAlphaPixels, 0, 16, 0xff, 0, 0, 0xff00),
        56 => new(PfLuminance, 0, 16, 0xffff, 0, 0, 0),
        61 => new(PfLuminance, 0, 8, 0xff, 0, 0, 0),
        65 => new(PfAlpha, 0, 8, 0, 0, 0, 0xff),
        68 => Legacy(FourCc("RGBG")),
        69 => Legacy(FourCc("GRGB")),
        71 => Legacy(FourCc("DXT1")),
        74 => Legacy(FourCc("DXT3")),
        77 => Legacy(FourCc("DXT5")),
        80 => Legacy(FourCc("BC4U")),
        81 => Legacy(FourCc("BC4S")),
        83 => Legacy(FourCc("BC5U")),
        84 => Legacy(FourCc("BC5S")),
        85 => new(PfRgb, 0, 16, 0xf800, 0x7e0, 0x1f, 0),
        86 => new(PfRgb | PfAlphaPixels, 0, 16, 0x7c00, 0x3e0, 0x1f, 0x8000),
        51 => new(PfBump, 0, 16, 0xff, 0xff00, 0, 0),
        31 => new(PfBump, 0, 32, 0xff, 0xff00, 0xff0000, 0xff000000),
        37 => new(PfBump, 0, 32, 0xffff, 0xffff0000, 0, 0),
        87 => new(PfRgb | PfAlphaPixels, 0, 32, 0xff0000, 0xff00, 0xff, 0xff000000),
        88 => new(PfRgb, 0, 32, 0xff0000, 0xff00, 0xff, 0),
        115 => new(PfRgb | PfAlphaPixels, 0, 16, 0xf00, 0xf0, 0xf, 0xf000),
        107 => Legacy(FourCc("YUY2")),
        _ => null,
    };

    // Mirrors DirectXTex ComputePitch. A format it has no rule for gets a pitch of zero, as upstream
    // writes it, rather than failing the whole archive.
    private static (ulong RowPitch, ulong SlicePitch, bool Compressed) Pitch(uint format, uint width, uint height)
    {
        // BC1 and BC4 blocks are 8 bytes, the other BCn blocks 16.
        if (format is >= 70 and <= 84 or >= 94 and <= 99)
        {
            var blockBytes = format is >= 70 and <= 72 or >= 79 and <= 81 ? 8UL : 16UL;
            var row = Math.Max(1UL, (width + 3UL) / 4) * blockBytes;
            return (row, row * Math.Max(1UL, (height + 3UL) / 4), true);
        }

        // Packed YUV: two pixels share a block.
        if (format is 68 or 69 or 107)
        {
            var row = ((width + 1UL) >> 1) * 4;
            return (row, row * height, false);
        }

        if (format is 108 or 109)
        {
            var row = ((width + 1UL) >> 1) * 8;
            return (row, row * height, false);
        }

        // Planar video: the chroma plane follows the luma plane, so the slice is taller than the image.
        if (format is 103 or 106)
        {
            var row = ((width + 1UL) >> 1) * 2;
            return (row, row * (height + ((height + 1UL) >> 1)), false);
        }

        if (format is 104 or 105)
        {
            var row = ((width + 1UL) >> 1) * 4;
            return (row, row * (height + ((height + 1UL) >> 1)), false);
        }

        if (format == 110)
        {
            var row = ((width + 3UL) >> 2) * 4;
            return (row, row * height * 2, false);
        }

        var rowBytes = (width * BitsPerPixel(format) + 7) / 8;
        return (rowBytes, rowBytes * height, false);
    }

    private static ulong BitsPerPixel(uint format) => format switch
    {
        >= 1 and <= 4 => 128,
        >= 5 and <= 8 => 96,
        >= 9 and <= 22 or 102 => 64,
        >= 23 and <= 47 or 67 or >= 87 and <= 93 or 100 or 101 => 32,
        >= 48 and <= 59 or 85 or 86 or 114 or 115 => 16,
        >= 60 and <= 65 or >= 111 and <= 113 => 8,
        66 => 1,
        _ => 0,
    };
}
