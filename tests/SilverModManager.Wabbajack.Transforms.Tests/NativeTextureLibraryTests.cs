using System.Runtime.InteropServices;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using SilverModManager.Sdk.Native;
using SilverModManager.Wabbajack.Transforms;

namespace SilverModManager.Wabbajack.Transforms.Tests;

/// <summary>
/// Loads the committed libsmmtexenc.so straight through <see cref="NativeLibrary"/>, checks its dynamic symbol table
/// against the export list and that every export encodes. It cannot tell whether the binary matches the current
/// extern/texenc sources; rebuild with scripts/build-texenc.sh after changing them.
/// </summary>
[Collection(NativeTexenc.Collection)]
public sealed unsafe class NativeTextureLibraryTests : IDisposable
{
    private static readonly string[] Exports =
    [
        "smm_texenc_init",
        "smm_texenc_encode_bc1",
        "smm_texenc_encode_bc3",
        "smm_texenc_encode_bc4",
        "smm_texenc_encode_bc5",
        "smm_texenc_encode_bc7",
    ];

    // The shim's own contract: the level a format takes is checked there, so the managed side cannot pass garbage through.
    private const int Bc7Levels = 7;
    private const int RgbcxLevels = 19;

    private static readonly string LibraryPath = Path.Combine(AppContext.BaseDirectory, "runtimes", BundledNativeLibrary.RuntimeIdentifiers()[^1], "native", "libsmmtexenc.so");

    // A platform with no bundled library is a supported configuration (the managed encoder is the floor), so these skip there.
    private readonly nint _library = File.Exists(LibraryPath) ? NativeLibrary.Load(LibraryPath) : 0;

    public void Dispose()
    {
        if (_library != 0) NativeLibrary.Free(_library);
    }

    // Only a platform with no bundled library may skip; on linux-x64 a missing file is a failure.
    private void RequireLibrary()
    {
        if (NativeTexenc.LibraryIsBundledForThisPlatform) Assert.True(_library != 0, $"{LibraryPath} is missing from the test output");
        else Assert.SkipUnless(_library != 0, $"{LibraryPath} is not bundled for this platform");
    }

    private nint Export(string name) => NativeLibrary.GetExport(_library, name);

    [Fact]
    public void ExportsEverySymbolTheManagedSideBinds()
    {
        RequireLibrary();
        Assert.All(Exports, name => Assert.True(NativeLibrary.TryGetExport(_library, name, out var address) && address != 0, name));
    }

    [Fact]
    public void ExportsNothingBeyondTheShim()
    {
        RequireLibrary();
        // bc7e's per-target entry points, its params_init functions and rgbcx's C++ symbols stay private, so the list
        // above is the whole surface. Read straight from .dynsym so no name has to be guessed.
        Assert.Equal(Exports.Order(StringComparer.Ordinal), DefinedDynamicSymbols(LibraryPath).Order(StringComparer.Ordinal));
    }

    // Names of the defined (section index != UND) entries of an ELF64 little-endian file's .dynsym, of every binding and type.
    private static List<string> DefinedDynamicSymbols(string path)
    {
        var file = File.ReadAllBytes(path);
        var sectionTable = (long)BitConverter.ToUInt64(file, 0x28);
        var entrySize = BitConverter.ToUInt16(file, 0x3A);
        var count = BitConverter.ToUInt16(file, 0x3C);
        const uint dynsym = 11;
        var names = new List<string>();
        for (var s = 0; s < count; s++)
        {
            var header = sectionTable + (long)s * entrySize;
            if (BitConverter.ToUInt32(file, (int)header + 4) != dynsym)
                continue;
            var offset = (long)BitConverter.ToUInt64(file, (int)header + 0x18);
            var size = (long)BitConverter.ToUInt64(file, (int)header + 0x20);
            var strtab = sectionTable + (long)BitConverter.ToUInt32(file, (int)header + 0x28) * entrySize;
            var strOffset = (long)BitConverter.ToUInt64(file, (int)strtab + 0x18);
            for (var sym = offset + 24; sym < offset + size; sym += 24)
            {
                if (BitConverter.ToUInt16(file, (int)sym + 6) == 0)
                    continue;
                var start = (int)(strOffset + BitConverter.ToUInt32(file, (int)sym));
                var end = Array.IndexOf(file, (byte)0, start);
                names.Add(System.Text.Encoding.ASCII.GetString(file, start, end - start));
            }
        }
        return names;
    }

    private int Encode(string export, byte[] rgba, int width, int rows, byte[] dst, uint level)
    {
        fixed (byte* src = rgba)
        fixed (byte* d = dst)
            return ((delegate* unmanaged<byte*, uint, uint, byte*, uint, int>)Export(export))(src, (uint)width, (uint)rows, d, level);
    }

    private void Init() => ((delegate* unmanaged<void>)Export("smm_texenc_init"))();

    // Wide enough that the shim's block scratch (a fixed number of blocks) is crossed more than once per block row.
    private const int Width = 4 * 150;
    private const int Rows = 8;

    private static byte[] Texture()
    {
        var rgba = new byte[Width * Rows * 4];
        for (var y = 0; y < Rows; y++)
        for (var x = 0; x < Width; x++)
        {
            var i = (y * Width + x) * 4;
            rgba[i] = (byte)(x * 255 / (Width - 1));
            rgba[i + 1] = (byte)(255 - y * 30);
            rgba[i + 2] = (byte)(255 - x * 255 / (Width - 1));
            // Alpha varies in both directions and stays below 255 so bc3's alpha block and bc7e's alpha modes are used.
            rgba[i + 3] = (byte)(40 + x * 150 / (Width - 1) + y * 8);
        }
        return rgba;
    }

    public static TheoryData<string, CompressionFormat, int, uint, int> Formats => new()
    {
        { "smm_texenc_encode_bc1", CompressionFormat.Bc1, 8, 10, 3 },
        { "smm_texenc_encode_bc3", CompressionFormat.Bc3, 16, 10, 4 },
        { "smm_texenc_encode_bc4", CompressionFormat.Bc4, 8, 0, 1 },
        { "smm_texenc_encode_bc5", CompressionFormat.Bc5, 16, 0, 2 },
        { "smm_texenc_encode_bc7", CompressionFormat.Bc7, 16, 0, 4 },
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void EncodesEveryBlockOfARowBandToTheRightSizeAndPlace(string export, CompressionFormat format, int blockBytes, uint level, int channels)
    {
        RequireLibrary();
        Init();
        var rgba = Texture();
        var dst = new byte[Width / 4 * (Rows / 4) * blockBytes];

        Assert.Equal(0, Encode(export, rgba, Width, Rows, dst, level));

        var decoded = new BcDecoder().DecodeRaw(dst, Width, Rows, format);
        for (var i = 0; i < Width * Rows; i++)
        {
            var expected = new[] { rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3] };
            var actual = new[] { decoded[i].r, decoded[i].g, decoded[i].b, decoded[i].a };
            for (var c = 0; c < channels; c++)
                Assert.InRange(Math.Abs(expected[c] - actual[c]), 0, 40);
        }
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void RejectsALevelOrShapeItCannotEncode(string export, CompressionFormat format, int blockBytes, uint level, int channels)
    {
        RequireLibrary();
        Init();
        _ = format; _ = channels;
        var rgba = Texture();
        var dst = new byte[Width / 4 * (Rows / 4) * blockBytes];

        Assert.NotEqual(0, Encode(export, rgba, Width - 2, Rows, dst, level));
        Assert.NotEqual(0, Encode(export, rgba, Width, Rows - 1, dst, level));
        if (export is "smm_texenc_encode_bc1" or "smm_texenc_encode_bc3")
            Assert.NotEqual(0, Encode(export, rgba, Width, Rows, dst, (uint)RgbcxLevels));
        if (export is "smm_texenc_encode_bc7")
            Assert.NotEqual(0, Encode(export, rgba, Width, Rows, dst, (uint)Bc7Levels));
    }
}
