using System.Collections.Immutable;

namespace NexusMods.Games.FromSoft.Formats;

/// <summary>How a game's binder archives (BND3 or BND4) are laid out.</summary>
public enum BinderFormat
{
    Bnd3,
    Bnd4,
}

/// <summary>The compression wrapper (DCX) around a file, named by its four-character magic.</summary>
public enum DcxCompression
{
    /// <summary>The file is not wrapped.</summary>
    None,

    /// <summary>zlib (<c>DFLT</c>).</summary>
    Dflt,

    /// <summary>Oodle Kraken (<c>KRAK</c>). Readable only; nothing here writes it.</summary>
    Kraken,

    /// <summary>Zstandard (<c>ZSTD</c>).</summary>
    Zstd,
}

/// <summary>
/// The file format parameters that differ from one FromSoft game to the next. Nothing reads them yet: they are the
/// values a binder, DCX or regulation reader would otherwise hard-code for Elden Ring.
/// </summary>
/// <param name="Binder">The binder format of the game's archives.</param>
/// <param name="ReadableCompressions">The wrappers found in the game's files; a reader must accept each.</param>
/// <param name="PackedWriteCompression">The wrapper a rewritten archive gets.</param>
/// <param name="RegulationFileName">The file in the game folder that holds the param tables; null when the game has none.</param>
/// <param name="RegulationCompression">The wrapper inside the regulation's encryption.</param>
/// <param name="RegulationZstdLevel">The Zstandard level a rewritten regulation is compressed at; 0 when it is not Zstandard.</param>
/// <param name="RegulationZstdWindowLog">The Zstandard window size, as a power of two, that caps what the game's decoder must allocate; 0 when it is not Zstandard.</param>
/// <param name="RegulationKey">The AES-256 key of the regulation. It ships with the game; empty when the regulation is not encrypted.</param>
/// <param name="ParamdefSet">
/// Names the family of param layouts the game uses. The param layout catalogue will be keyed by this and the build
/// from <see cref="Build.BuildIdentity"/>; the catalogue itself does not exist yet.
/// </param>
public sealed record FromSoftFormatTraits(
    BinderFormat Binder,
    ImmutableArray<DcxCompression> ReadableCompressions,
    DcxCompression PackedWriteCompression,
    string? RegulationFileName,
    DcxCompression RegulationCompression,
    int RegulationZstdLevel,
    int RegulationZstdWindowLog,
    ImmutableArray<byte> RegulationKey,
    string ParamdefSet);
