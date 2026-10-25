using System.Text.Json;
using System.Text.Json.Serialization;

namespace SilverModManager.Games.FromSoft.Heal;

/// <summary>How far a kept regulation is trusted as the ancestor of a merge.</summary>
public enum BaselineTier
{
    /// <summary>The file is the game's own: it matches a Steam depot manifest, was copied out of an install, or is in the hash database.</summary>
    Vouched,

    /// <summary>The file carries the right build stamp and passed the checks for a vanilla, but nothing outside the file confirms it. It may be a merge ancestor, but never vouches for a library archive.</summary>
    Validated,
}

/// <summary>Where a kept regulation came from.</summary>
public enum BaselineSource
{
    /// <summary>Copied out of the game folder.</summary>
    InstalledGame,

    /// <summary>A file whose SHA-1 is in a Steam depot manifest.</summary>
    SteamManifest,

    /// <summary>A member of an archive in the library.</summary>
    LibraryArchive,

    /// <summary>A file the user pointed at.</summary>
    UserFile,
}

/// <summary>
/// What is known about a kept regulation, written beside it as <c>regulation-&lt;version&gt;.bin.provenance</c>. The
/// tier decides whether the file may vouch for anything; the rest is what the merges page prints so a vanilla that
/// was only validated is never shown as if the game produced it.
/// </summary>
/// <param name="Tier">How far the file is trusted.</param>
/// <param name="Source">Where it came from.</param>
/// <param name="Sha1">SHA-1 of the file as stored, lowercase hex: the digest Steam's manifests list.</param>
/// <param name="Sha256">SHA-256 of the file as stored, lowercase hex: the digest a build identity carries.</param>
/// <param name="Xxh3">The xxHash3 the app's file store and hash lookups use, as 16 lowercase hex digits.</param>
/// <param name="RecordedAt">When the file was kept.</param>
/// <param name="ManifestId">The Steam manifest that lists the file, when one does.</param>
/// <param name="ManifestMatched">Whether a manifest was searched for the file and found: false means Steam could not confirm it, null means no search was made.</param>
/// <param name="OriginalPath">The path the file was read from, for a file the user pointed at.</param>
/// <param name="Validation">What the checks of a validated file found.</param>
/// <param name="Replaces">SHA-256 of the validated file this one replaced.</param>
public sealed record RegulationProvenance(
    [property: JsonRequired] BaselineTier Tier,
    [property: JsonRequired] BaselineSource Source,
    [property: JsonRequired] string Sha1,
    [property: JsonRequired] string Sha256,
    [property: JsonRequired] string Xxh3,
    [property: JsonRequired] DateTimeOffset RecordedAt,
    ulong? ManifestId = null,
    bool? ManifestMatched = null,
    string? OriginalPath = null,
    string? Validation = null,
    string? Replaces = null)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.KebabCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    /// <summary>A phrase for where the file came from, for the merges page: a validated file is never described as if the game produced it.</summary>
    public string Describe() => (Tier, Source) switch
    {
        (BaselineTier.Vouched, BaselineSource.SteamManifest) => $"the vanilla regulation vouched by Steam manifest {ManifestId}",
        (BaselineTier.Vouched, BaselineSource.InstalledGame) => "the vanilla regulation kept from the installed game",
        (BaselineTier.Validated, BaselineSource.LibraryArchive) => $"a vanilla regulation validated from a library archive{Detail()}",
        (BaselineTier.Validated, BaselineSource.UserFile) => $"a vanilla regulation validated from {OriginalPath ?? "a file you pointed at"}{Detail()}",
        (BaselineTier.Vouched, _) => "a vouched vanilla regulation",
        _ => $"a validated vanilla regulation{Detail()}",
    };

    private string Detail() => string.IsNullOrEmpty(Validation) ? "" : $" ({Validation})";

    /// <summary>The sidecar's text.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Reads a sidecar's text.</summary>
    /// <exception cref="JsonException">The text is not a provenance, or lacks a required field.</exception>
    public static RegulationProvenance Parse(string json)
        => JsonSerializer.Deserialize<RegulationProvenance>(json, Options)
           ?? throw new JsonException("the provenance is empty");
}
