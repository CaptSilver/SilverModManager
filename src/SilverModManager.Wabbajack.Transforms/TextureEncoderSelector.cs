using Microsoft.Extensions.Logging;
using SilverModManager.Sdk.Native;

namespace SilverModManager.Wabbajack.Transforms;

/// <summary>
/// Decides once which encoders a process uses: the native library first when it loads and exports everything the shim
/// promises, BCnEncoder.Net always last. The native encoder is optional on every platform; a platform with no bundled
/// library gets the managed one and one log line saying where it looked.
/// </summary>
public sealed class TextureEncoderSelector
{
    public const string LibraryFileName = "libsmmtexenc.so";

    public TextureEncoderSelector(INativeLibraryLoader loader, ILogger<TextureEncoderSelector> logger)
    {
        var managed = new ManagedTextureEncoder();
        Encoders = TryNative(loader, logger, managed.Name) is { } native ? [native, managed] : [managed];
    }

    /// <summary>In order of preference: the first that can encode a <see cref="BlockFormat"/> is used for it.</summary>
    public IReadOnlyList<ITextureEncoder> Encoders { get; }

    private static NativeTextureEncoder? TryNative(INativeLibraryLoader loader, ILogger logger, string fallbackName)
    {
        if (!loader.TryLoad(LibraryFileName, out var library, out var tried))
        {
            logger.LogInformation("Texture encoder: {Library} was not found, using {Encoder}. Looked in: {Paths}",
                LibraryFileName, fallbackName, string.Join(", ", tried));
            return null;
        }

        if (!NativeTextureEncoder.TryBind(loader, library, NativeEncoderLevels.Default, out var encoder, out var missing))
        {
            logger.LogWarning("Texture encoder: {Library} lacks {Exports}, so it is out of date; using {Encoder}",
                LibraryFileName, string.Join(", ", missing), fallbackName);
            return null;
        }

        logger.LogInformation("Texture encoders: {Native} ({Library}), then {Encoder}", encoder.Name, LibraryFileName, fallbackName);
        return encoder;
    }
}
