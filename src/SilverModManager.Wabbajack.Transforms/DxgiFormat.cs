// Names and values follow Wabbajack.DTOs/Texture/DXGI_FORMAT.cs; modlists record the format by name.
namespace SilverModManager.Wabbajack.Transforms;

/// <summary>
/// The DXGI texture formats a modlist can name as a re-encode target, plus the ones the decoder
/// recognises in an existing file. Which of them can actually be written is decided by the recompressor.
/// </summary>
public enum DxgiFormat : uint
{
    UNKNOWN = 0,
    R8G8B8A8_UNORM = 28,
    R8G8B8A8_UNORM_SRGB = 29,
    R8G8_UNORM = 49,
    R16_UNORM = 56,
    R8_UNORM = 61,
    A8_UNORM = 65,
    BC1_UNORM = 71,
    BC1_UNORM_SRGB = 72,
    BC2_UNORM = 74,
    BC2_UNORM_SRGB = 75,
    BC3_UNORM = 77,
    BC3_UNORM_SRGB = 78,
    BC4_UNORM = 80,
    BC4_SNORM = 81,
    BC5_UNORM = 83,
    BC5_SNORM = 84,
    B5G6R5_UNORM = 85,
    B5G5R5A1_UNORM = 86,
    B8G8R8A8_UNORM = 87,
    B8G8R8X8_UNORM = 88,
    B8G8R8A8_UNORM_SRGB = 91,
    B8G8R8X8_UNORM_SRGB = 93,
    BC6H_UF16 = 95,
    BC7_UNORM = 98,
    BC7_UNORM_SRGB = 99,
    B4G4R4A4_UNORM = 115,
}
