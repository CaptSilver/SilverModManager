using System.Text.Json.Serialization;

namespace SilverModManager.Networking.GameBanana.DTOs;

/// <summary>
/// One submission in a listing (<c>Subfeed</c>, <c>Index</c> or a search). The update stamp is missing on a submission
/// that was never updated.
/// </summary>
public sealed record GameBananaRecord(
    [property: JsonPropertyName("_idRow")] long Id,
    [property: JsonPropertyName("_sName")] string Name,
    [property: JsonPropertyName("_sModelName")] string? ModelName = null,
    [property: JsonPropertyName("_sProfileUrl")] string? ProfileUrl = null,
    [property: JsonPropertyName("_tsDateAdded")] long? DateAdded = null,
    [property: JsonPropertyName("_tsDateModified")] long? DateModified = null,
    [property: JsonPropertyName("_tsDateUpdated")] long? DateUpdated = null,
    [property: JsonPropertyName("_bHasFiles")] bool? HasFiles = null,
    [property: JsonPropertyName("_sVersion")] string? Version = null,
    [property: JsonPropertyName("_bHasContentRatings")] bool? HasContentRatings = null,
    [property: JsonPropertyName("_nViewCount")] long? ViewCount = null,
    [property: JsonPropertyName("_nLikeCount")] long? LikeCount = null,
    [property: JsonPropertyName("_aSubmitter")] GameBananaSubmitter? Submitter = null,
    [property: JsonPropertyName("_aPreviewMedia")] GameBananaPreviewMedia? PreviewMedia = null,
    [property: JsonPropertyName("_aGame")] GameBananaGameRef? Game = null,
    [property: JsonPropertyName("_aRootCategory")] GameBananaCategory? RootCategory = null);

/// <summary>A category a submission is filed under, such as Maps or HUDs.</summary>
public sealed record GameBananaCategory([property: JsonPropertyName("_sName")] string? Name = null);

/// <summary>The member who posted a submission.</summary>
public sealed record GameBananaSubmitter([property: JsonPropertyName("_sName")] string? Name = null);

/// <summary>The game a submission is listed under.</summary>
public sealed record GameBananaGameRef(
    [property: JsonPropertyName("_idRow")] long Id,
    [property: JsonPropertyName("_sName")] string? Name = null);

/// <summary>The pictures of a submission.</summary>
public sealed record GameBananaPreviewMedia([property: JsonPropertyName("_aImages")] IReadOnlyList<GameBananaImage>? Images = null)
{
    /// <summary>
    /// The address of the first picture at the site's listing size, or null when there is none.
    /// </summary>
    public Uri? Thumbnail
    {
        get
        {
            var image = Images?.FirstOrDefault();
            var file = image?.File220 ?? image?.File;
            return image?.BaseUrl is { } baseUrl && file is not null && Uri.TryCreate($"{baseUrl.TrimEnd('/')}/{file}", UriKind.Absolute, out var uri) ? uri : null;
        }
    }
}

/// <summary>One picture of a submission.</summary>
public sealed record GameBananaImage(
    [property: JsonPropertyName("_sBaseUrl")] string? BaseUrl = null,
    [property: JsonPropertyName("_sFile")] string? File = null,
    [property: JsonPropertyName("_sFile220")] string? File220 = null);
