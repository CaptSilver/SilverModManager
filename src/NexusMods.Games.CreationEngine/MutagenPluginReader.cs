using Mutagen.Bethesda;
using Mutagen.Bethesda.Fallout4;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Binary.Streams;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.FileStore;

namespace NexusMods.Games.CreationEngine;

/// <summary>
/// Reads plugin headers with the Mutagen package that matches a game release.
/// </summary>
public static class MutagenPluginReader
{
    private static readonly Mutagen.Bethesda.Skyrim.GroupMask SkyrimMask = new(false);
    private static readonly Mutagen.Bethesda.Fallout4.GroupMask Fallout4Mask = new(false);

    /// <summary>
    /// Parses the header of the plugin stored under <paramref name="hash"/>.
    /// </summary>
    /// <returns>The header, or <c>null</c> when no stream source can supply the bytes.</returns>
    /// <exception cref="NotSupportedException">No Mutagen package for <paramref name="release"/> is referenced.</exception>
    public static async ValueTask<IMod?> ReadHeader(IStreamSourceDispatcher streamSource, Hash hash, RelativePath? name, GameRelease release)
    {
        // Checked before opening the stream so an unsupported release never touches the store.
        if (!IsSupported(release)) throw new NotSupportedException($"No plugin reader is available for {release}.");

        var fileName = name?.FileName.ToString() ?? "unknown.esm";
        var key = ModKey.FromFileName(fileName);
        await using var stream = await streamSource.OpenAsync(hash);
        if (stream is null) return null;

        var meta = ParsingMeta.Factory(BinaryReadParameters.Default, release, key, stream);
        await using var mutagenStream = new MutagenBinaryReadStream(stream, meta);
        using var frame = new MutagenFrame(mutagenStream);

        return release switch
        {
            GameRelease.SkyrimLE or GameRelease.SkyrimSE or GameRelease.SkyrimVR or GameRelease.EnderalLE or GameRelease.EnderalSE
                => SkyrimMod.CreateFromBinary(frame, release.ToSkyrimRelease(), SkyrimMask),
            GameRelease.Fallout4 or GameRelease.Fallout4VR
                => Fallout4Mod.CreateFromBinary(frame, release.ToFallout4Release(), Fallout4Mask),
            _ => throw new NotSupportedException($"No plugin reader is available for {release}."),
        };
    }

    private static bool IsSupported(GameRelease release) => release is
        GameRelease.SkyrimLE or GameRelease.SkyrimSE or GameRelease.SkyrimVR or GameRelease.EnderalLE or GameRelease.EnderalSE
        or GameRelease.Fallout4 or GameRelease.Fallout4VR;
}
