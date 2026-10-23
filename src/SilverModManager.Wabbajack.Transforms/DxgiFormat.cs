// Names and values follow Wabbajack.DTOs/Texture/DXGI_FORMAT.cs; modlists record the format by name.
namespace SilverModManager.Wabbajack.Transforms;

/// <summary>
/// The DXGI texture formats a modlist can name as a re-encode target, plus the ones the decoder
/// recognises in an existing file. Only the block-compressed and 8-bit RGBA formats can be written.
/// </summary>
public enum DxgiFormat : uint
{
    UNKNOWN = 0,
    R8G8B8A8_UNORM = 28,
    BC1_UNORM = 71,
    BC2_UNORM = 74,
    BC3_UNORM = 77,
    BC4_UNORM = 80,
    BC5_UNORM = 83,
    B8G8R8A8_UNORM = 87,
    BC6H_UF16 = 95,
    BC7_UNORM = 98,
}
