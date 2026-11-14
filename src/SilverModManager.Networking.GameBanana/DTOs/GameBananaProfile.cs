using System.Text.Json;
using System.Text.Json.Serialization;

namespace SilverModManager.Networking.GameBanana.DTOs;

/// <summary>
/// A mod's page: its files and what the author allows. A private or withheld mod answers a stub that has only
/// <see cref="Name"/> and the two flags, which the client turns into <see cref="GameBananaUnavailableException"/>.
/// </summary>
public sealed record GameBananaProfile(
    [property: JsonPropertyName("_idRow")] long Id,
    [property: JsonPropertyName("_sName")] string Name,
    [property: JsonPropertyName("_bIsPrivate")] bool IsPrivate = false,
    [property: JsonPropertyName("_bIsWithheld")] bool IsWithheld = false,
    [property: JsonPropertyName("_sProfileUrl")] string? ProfileUrl = null,
    [property: JsonPropertyName("_tsDateModified")] long? DateModified = null,
    [property: JsonPropertyName("_tsDateUpdated")] long? DateUpdated = null,
    [property: JsonPropertyName("_sVersion")] string? Version = null,
    [property: JsonPropertyName("_aFiles")] IReadOnlyList<GameBananaFile>? Files = null,
    [property: JsonPropertyName("_aAlternateFileSources")] IReadOnlyList<GameBananaAlternateSource>? AlternateFileSources = null,
    [property: JsonPropertyName("_aLicenseChecklist"), JsonConverter(typeof(LicenseChecklistConverter))] GameBananaLicenseChecklist? LicenseChecklist = null,
    [property: JsonPropertyName("_aGame")] GameBananaGameRef? Game = null,
    [property: JsonPropertyName("_aSubmitter")] GameBananaSubmitter? Submitter = null,
    [property: JsonPropertyName("_aCategory")] GameBananaCategory? Category = null)
{
    /// <summary>The files, empty when the page lists none.</summary>
    public IReadOnlyList<GameBananaFile> AllFiles => Files ?? [];

    /// <summary>
    /// The page's update stamp, zero when it was never updated. Only this, with the file ids, says a newer version
    /// exists: <see cref="DateModified"/> moves on any edit, a description included.
    /// </summary>
    public long UpdateStamp => DateUpdated ?? 0;
}

/// <summary>One downloadable file of a mod page.</summary>
public sealed record GameBananaFile(
    [property: JsonPropertyName("_idRow")] long Id,
    [property: JsonPropertyName("_sFile")] string FileName,
    [property: JsonPropertyName("_nFilesize")] long? Size = null,
    [property: JsonPropertyName("_sDownloadUrl")] string? DownloadUrl = null,
    [property: JsonPropertyName("_sMd5Checksum")] string? Md5Checksum = null,
    [property: JsonPropertyName("_sAvResult")] string? AvResult = null,
    [property: JsonPropertyName("_tsDateAdded")] long? DateAdded = null,
    [property: JsonPropertyName("_nDownloadCount")] long? DownloadCount = null,
    [property: JsonPropertyName("_aAnalysisWarnings"), JsonConverter(typeof(AnalysisWarningsConverter))] IReadOnlyDictionary<string, IReadOnlyList<string>>? AnalysisWarnings = null)
{
    /// <summary>
    /// The address to fetch the file from; the stable <c>/dl/{id}</c> one when the page names none.
    /// </summary>
    public Uri Download => Uri.TryCreate(DownloadUrl, UriKind.Absolute, out var uri) ? uri : new Uri($"https://gamebanana.com/dl/{Id}");

    /// <summary>
    /// True only when the site's virus scan finished and found nothing.
    /// </summary>
    public bool IsClean => string.Equals(AvResult, "clean", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What the site's analysis flagged in the archive, such as <c>contains_exe: [Game.exe]</c>, as short lines.
    /// Empty when it flagged nothing.
    /// </summary>
    public IReadOnlyList<string> WarningLines
        => AnalysisWarnings is null
            ? []
            : [.. AnalysisWarnings.Where(pair => pair.Value.Count > 0).Select(pair => $"{pair.Key}: {string.Join(", ", pair.Value)}")];
}

/// <summary>A mirror an author posted outside the site: a drive folder, a magnet link, a source repository.</summary>
public sealed record GameBananaAlternateSource(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("description")] string? Description = null);

/// <summary>What the author allows, by the site's own wording of each permission.</summary>
public sealed record GameBananaLicenseChecklist(
    [property: JsonPropertyName("yes")] IReadOnlyList<string>? Yes = null,
    [property: JsonPropertyName("ask")] IReadOnlyList<string>? Ask = null,
    [property: JsonPropertyName("no")] IReadOnlyList<string>? No = null)
{
    /// <summary>The permission the author withholds when they forbid installing the mod at all.</summary>
    public const string DownloadAndInstall = "Download and install this Mod";

    /// <summary>True when the author lists installing the mod among the things not allowed.</summary>
    public bool ForbidsInstalling => No?.Any(entry => entry.Contains(DownloadAndInstall, StringComparison.OrdinalIgnoreCase)) == true;
}

// The site answers an empty object as an empty array, so a plain object type cannot read every reply.
internal sealed class LicenseChecklistConverter : JsonConverter<GameBananaLicenseChecklist?>
{
    public override GameBananaLicenseChecklist? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return null;
        }

        return JsonSerializer.Deserialize<LicenseChecklistShape>(ref reader) is { } shape
            ? new GameBananaLicenseChecklist(shape.Yes, shape.Ask, shape.No)
            : null;
    }

    public override void Write(Utf8JsonWriter writer, GameBananaLicenseChecklist? value, JsonSerializerOptions options) => throw new NotSupportedException();
}

internal sealed record LicenseChecklistShape(
    [property: JsonPropertyName("yes")] IReadOnlyList<string>? Yes,
    [property: JsonPropertyName("ask")] IReadOnlyList<string>? Ask,
    [property: JsonPropertyName("no")] IReadOnlyList<string>? No);

internal sealed class AnalysisWarningsConverter : JsonConverter<IReadOnlyDictionary<string, IReadOnlyList<string>>?>
{
    public override IReadOnlyDictionary<string, IReadOnlyList<string>>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return null;
        }

        var warnings = new Dictionary<string, IReadOnlyList<string>>();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var key = reader.GetString() ?? "";
            reader.Read();
            if (reader.TokenType == JsonTokenType.StartArray)
            {
                var values = new List<string>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType == JsonTokenType.String && reader.GetString() is { } text) values.Add(text);
                    else reader.Skip();
                }

                warnings[key] = values;
            }
            else
            {
                reader.Skip();
            }
        }

        return warnings;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<string, IReadOnlyList<string>>? value, JsonSerializerOptions options) => throw new NotSupportedException();
}
