using SilverModManager.Sdk.Native;
using SilverModManager.Wabbajack.Transforms;

namespace SilverModManager.Benchmarks.Benchmarks.Textures;

internal static class NativeEncoders
{
    /// <summary>The bundled library bound at <paramref name="levels"/>; throws when it does not load, since a benchmark of the fallback would mislead.</summary>
    public static NativeTextureEncoder At(NativeEncoderLevels levels)
    {
        var loader = new BundledNativeLibraryLoader(typeof(NativeTextureEncoder).Assembly);
        if (!loader.TryLoad(TextureEncoderSelector.LibraryFileName, out var library, out var tried, out var failures))
            throw new InvalidOperationException($"{TextureEncoderSelector.LibraryFileName} did not load. Looked in {string.Join(", ", tried)}. {string.Join("; ", failures)}");
        if (!NativeTextureEncoder.TryBind(loader, library, levels, out var encoder, out var missing))
            throw new InvalidOperationException($"{TextureEncoderSelector.LibraryFileName} lacks {string.Join(", ", missing)}");
        return encoder;
    }

    /// <summary>The level that applies to <paramref name="format"/>, as the one <see cref="NativeEncoderLevels"/> field to set.</summary>
    public static NativeEncoderLevels With(BlockFormat format, uint level) => format switch
    {
        BlockFormat.Bc1 => NativeEncoderLevels.Default with { Bc1 = level },
        BlockFormat.Bc3 => NativeEncoderLevels.Default with { Bc3 = level },
        BlockFormat.Bc7 => NativeEncoderLevels.Default with { Bc7 = level },
        _ => NativeEncoderLevels.Default,
    };
}
