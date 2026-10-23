using System.Text.Json;
using NexusMods.Paths;

namespace NexusMods.Wabbajack.Format;

/// <summary>
/// Reads the <c>modlist</c> JSON of a <c>.wabbajack</c> file. The <c>$type</c> switches are written by hand because
/// Wabbajack names the same type two ways (<c>FromArchive, Wabbajack.Lib</c> and <c>FromArchive</c>) depending on
/// the version that wrote the list.
/// </summary>
public static class ModListReader
{
    private const string LongNameSuffix = ", Wabbajack.Lib";

    /// <exception cref="UnsupportedModlistException">
    /// The input is not JSON, lacks a required field, or holds a directive type this reader does not know.
    /// </exception>
    public static ModList Read(Stream modlistJson)
    {
        try
        {
            using var document = JsonDocument.Parse(modlistJson);
            return ReadModList(new JsonObject(document.RootElement, "The modlist"));
        }
        catch (JsonException e)
        {
            throw new UnsupportedModlistException($"The modlist is not valid JSON: {e.Message}");
        }
        catch (Exception e) when (e is InvalidOperationException or OverflowException or FormatException)
        {
            throw new UnsupportedModlistException($"The modlist is malformed: {e.Message}");
        }
    }

    private static ModList ReadModList(JsonObject root)
    {
        var archives = root.Array("Archives").Select((e, i) => ReadArchive(new JsonObject(e, $"Archive {i}"))).ToArray();
        var directives = root.Array("Directives").Select((e, i) => ReadDirective(e, i)).ToArray();

        return new ModList(
            Name: root.String("Name"),
            Author: root.OptionalString("Author") ?? "",
            Description: root.OptionalString("Description") ?? "",
            Version: root.OptionalString("Version") ?? "1.0.0.0",
            WabbajackVersion: root.OptionalString("WabbajackVersion"),
            GameType: root.String("GameType"),
            OtherGames: root.OptionalArray("OtherGames")?.Select(g => g.GetString() ?? "").ToArray() ?? [],
            Image: root.OptionalString("Image") ?? "",
            Readme: root.OptionalString("Readme") ?? "",
            Website: root.OptionalString("Website") ?? "",
            Community: root.OptionalString("Community") ?? "",
            IsNsfw: root.Bool("IsNSFW"),
            Archives: archives,
            Directives: directives);
    }

    private static ArchiveEntry ReadArchive(JsonObject archive) => new(
        XxHash64: archive.Hash("Hash"),
        Size: archive.Long("Size"),
        Name: archive.String("Name"),
        Meta: archive.OptionalString("Meta") ?? "",
        State: ReadState(archive.Object("State")));

    private static string Normalize(string typeName) =>
        typeName.EndsWith(LongNameSuffix, StringComparison.Ordinal) ? typeName[..^LongNameSuffix.Length] : typeName;

    private static DownloadState ReadState(JsonObject state)
    {
        var typeName = state.String("$type");
        return Normalize(typeName) switch
        {
            "NexusDownloader" or "Nexus" => new NexusState(
                GameName: state.OptionalString("GameName") ?? state.String("Game"),
                ModId: state.Long("ModID"),
                FileId: state.Long("FileID"),
                Name: state.OptionalString("Name"),
                Version: state.OptionalString("Version"),
                Author: state.OptionalString("Author"),
                IsNsfw: state.Bool("IsNSFW")),
            "HttpDownloader" or "Http" => WithUrl(state, typeName, url => new HttpState(
                url, state.OptionalArray("Headers")?.Select(h => h.GetString() ?? "").ToArray() ?? [])),
            "WabbajackCDNDownloader+State" or "WabbajackCDN" => WithUrl(state, typeName, url => new WabbajackCdnState(url)),
            "GameFileSourceDownloader" or "GameFileSource" => new GameFileSourceState(
                Game: state.String("Game"),
                GameFile: state.Path("GameFile"),
                XxHash64: state.Hash("Hash"),
                GameVersion: state.OptionalString("GameVersion") ?? ""),
            "ManualDownloader" or "Manual" => WithUrl(state, typeName, url => new ManualState(url, state.OptionalString("Prompt") ?? "")),
            "MegaDownloader" or "Mega" => WithUrl(state, typeName, url => new MegaState(url)),
            "GoogleDriveDownloader" or "GoogleDrive" => new GoogleDriveState(state.String("Id")),
            "MediaFireDownloader+State" or "MediaFireDownloader" or "MediaFire" => WithUrl(state, typeName, url => new MediaFireState(url)),
            "ModDBDownloader" or "ModDB" => WithUrl(state, typeName, url => new ModDbState(url)),
            "LoversLabOAuthDownloader" or "LoversLab" => new LoversLabState(IpsUrl(state), state.OptionalString("IPS4File")),
            "VectorPlexusOAuthDownloader+State" or "VectorPlexus" => new VectorPlexusState(IpsUrl(state), state.OptionalString("IPS4File")),
            _ => new UnsupportedState(typeName, state.OptionalString("Url")),
        };
    }

    /// <summary>
    /// A state whose URL is not a usable absolute URL is one the app cannot fetch, not a reason to refuse the list.
    /// </summary>
    private static DownloadState WithUrl(JsonObject state, string typeName, Func<Uri, DownloadState> create)
    {
        var raw = state.String("Url");
        return Uri.TryCreate(raw, UriKind.Absolute, out var url) ? create(url) : new UnsupportedState(typeName, raw);
    }

    private static Uri? IpsUrl(JsonObject state) =>
        Uri.TryCreate(state.OptionalString("IPS4Url"), UriKind.Absolute, out var url) ? url : null;

    private static Directive ReadDirective(JsonElement element, int index)
    {
        var probe = new JsonObject(element, $"Directive {index}");
        var typeName = probe.String("$type");
        var d = new JsonObject(element, $"Directive {index} ({typeName})");

        var to = d.Path("To");
        var hash = d.Hash("Hash");
        var size = d.Long("Size");

        switch (Normalize(typeName))
        {
            case "FromArchive":
                return new FromArchive(to, hash, size, ReadArchiveHashPath(d));
            case "PatchedFromArchive":
                return new PatchedFromArchive(to, hash, size, ReadArchiveHashPath(d), d.Hash("FromHash"), d.String("PatchID"));
            case "TransformedTexture":
            {
                var image = d.Object("ImageState");
                var format = image.Required("Format");
                return new TransformedTexture(to, hash, size, ReadArchiveHashPath(d), new ImageState(
                    image.Int("Width"), image.Int("Height"), image.Int("MipLevels"),
                    format.ValueKind == JsonValueKind.String ? StringOf(format) : format.GetRawText()));
            }
            case "InlineFile":
                return new InlineFile(to, hash, size, d.String("SourceDataID"));
            case "RemappedInlineFile":
                return new RemappedInlineFile(to, hash, size, d.String("SourceDataID"));
            case "PropertyFile":
                return new PropertyFile(to, hash, size, d.String("SourceDataID"), ReadPropertyKind(d));
            case "CreateBSA":
                // Cloned: the elements must outlive the document they were parsed from.
                return new CreateBsa(to, hash, size, d.Path("TempID"),
                    d.Required("State").Clone(),
                    d.Array("FileStates").Select(e => e.Clone()).ToArray());
            case "MergedPatch":
                return new MergedPatch(to, hash, size, d.String("PatchID"), d.Array("Sources")
                    .Select((e, i) => ReadMergedSource(new JsonObject(e, $"{typeName} source {i} of directive {index}")))
                    .ToArray());
            case "ArchiveMeta":
                return new ArchiveMeta(to, hash, size, d.String("SourceDataID"));
            default:
                throw new UnsupportedModlistException($"Directive {index} has the type '{typeName}', which this app cannot install");
        }
    }

    private static MergedPatchSource ReadMergedSource(JsonObject source) =>
        new(source.Hash("Hash"), source.Path("RelativePath"));

    private static ArchiveHashPath ReadArchiveHashPath(JsonObject d)
    {
        var parts = d.Array("ArchiveHashPath").ToArray();
        if (parts.Length == 0)
            throw d.Malformed("ArchiveHashPath", "is empty");
        if (parts.Any(p => p.ValueKind != JsonValueKind.String))
            throw d.Malformed("ArchiveHashPath", "holds something other than strings");

        return new ArchiveHashPath(
            d.ParseHash("ArchiveHashPath", StringOf(parts[0])),
            parts.Skip(1).Select(p => ModlistPath.Sanitize(StringOf(p))).ToArray());
    }

    /// <summary>A string element's value; callers have already checked the kind.</summary>
    private static string StringOf(JsonElement element) =>
        element.GetString() ?? throw new UnsupportedModlistException($"Expected a string, found {element.ValueKind}");

    private static PropertyKind ReadPropertyKind(JsonObject d)
    {
        var value = d.Required("Type");
        if (value.ValueKind == JsonValueKind.String && Enum.TryParse<PropertyKind>(value.GetString(), ignoreCase: true, out var named))
            return named;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && Enum.IsDefined(typeof(PropertyKind), number))
            return (PropertyKind)number;
        throw d.Malformed("Type", $"is not a property kind ({value.GetRawText()})");
    }
}
