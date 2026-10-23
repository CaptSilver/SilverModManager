using System.Text.Json;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;

namespace NexusMods.Wabbajack.Format;

/// <summary>
/// One instruction of the list: put a file at <paramref name="To"/> (relative to the MO2 instance root).
/// <paramref name="XxHash64"/> and <paramref name="Size"/> describe the finished file.
/// </summary>
public abstract record Directive(RelativePath To, Hash XxHash64, long Size);

/// <summary>Copy the member at <paramref name="Source"/>.</summary>
public record FromArchive(RelativePath To, Hash XxHash64, long Size, ArchiveHashPath Source) : Directive(To, XxHash64, Size);

public sealed record PatchedFromArchive(RelativePath To, Hash XxHash64, long Size, ArchiveHashPath Source, Hash FromHash, string PatchId)
    : FromArchive(To, XxHash64, Size, Source);

public sealed record TransformedTexture(RelativePath To, Hash XxHash64, long Size, ArchiveHashPath Source, ImageState Image)
    : FromArchive(To, XxHash64, Size, Source);

/// <summary>Write the blob named <paramref name="SourceDataId"/> from the <c>.wabbajack</c> file.</summary>
public record InlineFile(RelativePath To, Hash XxHash64, long Size, string SourceDataId) : Directive(To, XxHash64, Size);

/// <summary>An inline file whose path markers (game, MO2 and downloads folders) are substituted when written.</summary>
public sealed record RemappedInlineFile(RelativePath To, Hash XxHash64, long Size, string SourceDataId)
    : InlineFile(To, XxHash64, Size, SourceDataId);

public sealed record PropertyFile(RelativePath To, Hash XxHash64, long Size, string SourceDataId, PropertyKind Kind)
    : InlineFile(To, XxHash64, Size, SourceDataId);

/// <summary>
/// Build a Bethesda archive from the files other directives put under <c>TEMP_BSA_FILES\TempId</c>. The states are
/// kept as JSON so this project does not depend on the archive library.
/// </summary>
public sealed record CreateBsa(RelativePath To, Hash XxHash64, long Size, RelativePath TempId, JsonElement State, JsonElement[] FileStates)
    : Directive(To, XxHash64, Size);

/// <summary>Concatenate the already installed <paramref name="Sources"/> and apply the patch blob to the result.</summary>
public sealed record MergedPatch(RelativePath To, Hash XxHash64, long Size, string PatchId, IReadOnlyList<MergedPatchSource> Sources)
    : Directive(To, XxHash64, Size);

/// <summary>A <c>.meta</c> file Wabbajack keeps beside a download; the app does not install it.</summary>
public sealed record ArchiveMeta(RelativePath To, Hash XxHash64, long Size, string SourceDataId) : Directive(To, XxHash64, Size);

public sealed record MergedPatchSource(Hash XxHash64, RelativePath Path);

public enum PropertyKind
{
    Banner,
    Readme,
}

/// <summary>A member of an archive: the archive's hash, then the path through nested archives. Parts is empty when the archive is itself the file.</summary>
public sealed record ArchiveHashPath(Hash ArchiveXxHash64, IReadOnlyList<RelativePath> Parts);

/// <summary>Target of a re-encoded texture. <paramref name="Format"/> is a DXGI_FORMAT name.</summary>
public sealed record ImageState(int Width, int Height, int MipLevels, string Format);
