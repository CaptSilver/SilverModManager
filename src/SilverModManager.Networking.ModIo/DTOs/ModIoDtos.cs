using System.Text.Json.Serialization;

namespace SilverModManager.Networking.ModIo;

/// <summary>One page of a mod.io listing. <paramref name="Total"/> is the whole listing's length, not the page's.</summary>
public sealed record ModIoPage<T>(IReadOnlyList<T> Items, int Total, int Offset);

/// <summary>
/// What a listing is asked for. <paramref name="Sort"/> is mod.io's own key, such as <c>-downloads_total</c>, where the
/// minus sign means descending.
/// </summary>
public sealed record ModIoQuery(string Text, string Sort, int Offset, int Limit);

/// <summary>A mod as mod.io's mod object has it, reduced to what the catalog and the update check read.</summary>
public sealed record ModIoMod(
    [property: JsonPropertyName("id")] uint Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("profile_url")] string? ProfileUrl,
    [property: JsonPropertyName("date_updated")] long DateUpdated,
    [property: JsonPropertyName("maturity_option")] int MaturityOption,
    [property: JsonPropertyName("logo")] ModIoLogo? Logo,
    [property: JsonPropertyName("submitted_by")] ModIoUserRef? SubmittedBy,
    [property: JsonPropertyName("modfile")] ModIoFile? Modfile,
    [property: JsonPropertyName("stats")] ModIoStats? Stats)
{
    /// <summary>Whether mod.io flags the mod as having mature content.</summary>
    public bool IsAdult => MaturityOption != 0;
}

public sealed record ModIoLogo([property: JsonPropertyName("thumb_320x180")] string? Thumb320x180);

public sealed record ModIoUserRef([property: JsonPropertyName("username")] string? Username);

public sealed record ModIoStats([property: JsonPropertyName("downloads_total")] long DownloadsTotal);

/// <summary>One uploaded file of a mod.</summary>
public sealed record ModIoFile(
    [property: JsonPropertyName("id")] uint Id,
    [property: JsonPropertyName("mod_id")] uint ModId,
    [property: JsonPropertyName("date_added")] long DateAdded,
    [property: JsonPropertyName("filesize")] long FileSize,
    [property: JsonPropertyName("filehash")] ModIoFileHash? FileHash,
    [property: JsonPropertyName("filename")] string FileName,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("download")] ModIoDownload? Download,
    [property: JsonPropertyName("virus_status")] int VirusStatus,
    [property: JsonPropertyName("virus_positive")] int VirusPositive)
{
    /// <summary>
    /// Whether mod.io finished scanning the file and found nothing. A scan that never ran finds nothing too, so a file
    /// mod.io has not scanned, is still scanning or could not scan is not clean.
    /// </summary>
    public bool IsClean => ScanProblem is null;

    /// <summary>Why the file is not clean, finishing "mod.io …"; null when it is.</summary>
    public string? ScanProblem => (VirusStatus, VirusPositive) switch
    {
        (1, 0) => null,
        (1, 1) => "found it malicious",
        (1, 2) => "found it potentially harmful",
        (1, var positive) => $"flagged it (result {positive})",
        (0, _) => "has not scanned it yet",
        (2, _) => "is still scanning it",
        (3, _) => "could not scan it: the file is too large",
        (4, _) => "could not find the file to scan",
        (5, _) => "failed to scan it",
        (var status, _) => $"reports scan status {status}, which this app does not know",
    };
}

public sealed record ModIoFileHash([property: JsonPropertyName("md5")] string? Md5);

/// <summary>
/// Where the file is fetched from. For a game that requires API-initiated downloads the address carries a hash that
/// expires at <paramref name="DateExpires"/> and cannot be saved and reused.
/// </summary>
public sealed record ModIoDownload(
    [property: JsonPropertyName("binary_url")] string BinaryUrl,
    [property: JsonPropertyName("date_expires")] long DateExpires);

/// <summary>The signed-in user, as <c>GET /me</c> answers.</summary>
public sealed record ModIoUser(
    [property: JsonPropertyName("id")] uint Id,
    [property: JsonPropertyName("username")] string? Username);
