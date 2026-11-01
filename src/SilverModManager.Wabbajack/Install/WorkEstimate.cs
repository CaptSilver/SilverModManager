using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;

namespace SilverModManager.Wabbajack.Install;

/// <summary>
/// How many bytes of memory producing a directive's file is expected to hold at its peak, so the install can keep the
/// total in flight under a <see cref="ByteBudget"/>. These are estimates for admitting work, not measurements.
/// </summary>
public static class WorkEstimate
{
    private const long Floor = 64 * 1024;
    private const long BuildCost = 64L * 1024 * 1024;
    private const long ReadLimit = 16L * 1024 * 1024;

    public static long Of(Directive directive, ResolvedMember? member) => directive switch
    {
        // Decoded to four bytes a pixel, resized, then encoded with its mip chain: a few copies of the full picture.
        TransformedTexture texture => Math.Max(Floor, 16L * texture.Image.Width * texture.Image.Height + 8L * (long)(member?.Size.Value ?? 0)),
        CreateBsa => BuildCost,
        PatchedFromArchive or MergedPatch => ReadLimit,
        _ => Math.Clamp(directive.Size, Floor, ReadLimit),
    };
}
