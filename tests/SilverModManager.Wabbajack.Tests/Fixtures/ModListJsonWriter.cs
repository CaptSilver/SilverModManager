using System.Text.Json;
using SilverModManager.Wabbajack.Format;

namespace SilverModManager.Wabbajack.Tests.Fixtures;

/// <summary>
/// Writes a <see cref="ModList"/> as the JSON Wabbajack itself produces (long type names), so the
/// reader is tested against the format and not only against its own output.
/// </summary>
public static class ModListJsonWriter
{
    public static byte[] Write(ModList list)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("$type", "ModList, Wabbajack.Lib");
            w.WriteString("Name", list.Name);
            w.WriteString("Author", list.Author);
            w.WriteString("Description", list.Description);
            w.WriteString("Version", list.Version);
            if (list.WabbajackVersion is not null) w.WriteString("WabbajackVersion", list.WabbajackVersion);
            w.WriteString("GameType", list.GameType);
            w.WriteStartArray("OtherGames");
            foreach (var game in list.OtherGames) w.WriteStringValue(game);
            w.WriteEndArray();
            w.WriteString("Image", list.Image);
            w.WriteString("Readme", list.Readme);
            w.WriteString("Website", list.Website);
            w.WriteString("Community", list.Community);
            w.WriteBoolean("IsNSFW", list.IsNsfw);

            w.WriteStartArray("Archives");
            foreach (var archive in list.Archives) WriteArchive(w, archive);
            w.WriteEndArray();

            w.WriteStartArray("Directives");
            foreach (var directive in list.Directives) WriteDirective(w, directive);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static string Path(NexusMods.Paths.RelativePath path) => path.ToString().Replace('/', '\\');

    private static void WriteArchive(Utf8JsonWriter w, ArchiveEntry archive)
    {
        w.WriteStartObject();
        w.WriteString("$type", "Archive, Wabbajack.Lib");
        w.WriteString("Hash", WabbajackHash.ToBase64(archive.XxHash64));
        w.WriteNumber("Size", archive.Size);
        w.WriteString("Name", archive.Name);
        w.WriteString("Meta", archive.Meta);
        w.WritePropertyName("State");
        WriteState(w, archive.State);
        w.WriteEndObject();
    }

    private static void WriteState(Utf8JsonWriter w, DownloadState state)
    {
        w.WriteStartObject();
        switch (state)
        {
            case NexusState s:
                w.WriteString("$type", "NexusDownloader, Wabbajack.Lib");
                w.WriteString("GameName", s.GameName);
                w.WriteNumber("ModID", s.ModId);
                w.WriteNumber("FileID", s.FileId);
                w.WriteString("Name", s.Name);
                w.WriteString("Version", s.Version);
                w.WriteString("Author", s.Author);
                w.WriteBoolean("IsNSFW", s.IsNsfw);
                break;
            case HttpState s:
                w.WriteString("$type", "HttpDownloader, Wabbajack.Lib");
                w.WriteString("Url", s.Url.ToString());
                w.WriteStartArray("Headers");
                foreach (var header in s.Headers) w.WriteStringValue(header);
                w.WriteEndArray();
                break;
            case WabbajackCdnState s:
                w.WriteString("$type", "WabbajackCDNDownloader+State, Wabbajack.Lib");
                w.WriteString("Url", s.Url.ToString());
                break;
            case GameFileSourceState s:
                w.WriteString("$type", "GameFileSourceDownloader, Wabbajack.Lib");
                w.WriteString("Game", s.Game);
                w.WriteString("GameFile", Path(s.GameFile));
                w.WriteString("Hash", WabbajackHash.ToBase64(s.XxHash64));
                w.WriteString("GameVersion", s.GameVersion);
                break;
            case ManualState s:
                w.WriteString("$type", "ManualDownloader, Wabbajack.Lib");
                w.WriteString("Url", s.Url.ToString());
                w.WriteString("Prompt", s.Prompt);
                break;
            default:
                throw new NotSupportedException($"The fixture writer has no form for {state.GetType().Name}");
        }
        w.WriteEndObject();
    }

    private static void WriteDirective(Utf8JsonWriter w, Directive directive)
    {
        w.WriteStartObject();
        w.WriteString("$type", directive switch
        {
            PatchedFromArchive => "PatchedFromArchive, Wabbajack.Lib",
            TransformedTexture => "TransformedTexture, Wabbajack.Lib",
            FromArchive => "FromArchive, Wabbajack.Lib",
            RemappedInlineFile => "RemappedInlineFile, Wabbajack.Lib",
            PropertyFile => "PropertyFile, Wabbajack.Lib",
            InlineFile => "InlineFile, Wabbajack.Lib",
            CreateBsa => "CreateBSA, Wabbajack.Lib",
            MergedPatch => "MergedPatch, Wabbajack.Lib",
            ArchiveMeta => "ArchiveMeta, Wabbajack.Lib",
            _ => throw new NotSupportedException($"The fixture writer has no form for {directive.GetType().Name}"),
        });
        w.WriteString("To", Path(directive.To));
        w.WriteString("Hash", WabbajackHash.ToBase64(directive.XxHash64));
        w.WriteNumber("Size", directive.Size);

        if (directive is FromArchive from)
        {
            w.WriteStartArray("ArchiveHashPath");
            w.WriteStringValue(WabbajackHash.ToBase64(from.Source.ArchiveXxHash64));
            foreach (var part in from.Source.Parts) w.WriteStringValue(Path(part));
            w.WriteEndArray();
        }

        switch (directive)
        {
            case PatchedFromArchive p:
                w.WriteString("FromHash", WabbajackHash.ToBase64(p.FromHash));
                w.WriteString("PatchID", p.PatchId);
                break;
            case TransformedTexture t:
                w.WriteStartObject("ImageState");
                w.WriteNumber("Width", t.Image.Width);
                w.WriteNumber("Height", t.Image.Height);
                w.WriteNumber("MipLevels", t.Image.MipLevels);
                w.WriteString("Format", t.Image.Format);
                w.WriteEndObject();
                break;
            case PropertyFile p:
                w.WriteString("SourceDataID", p.SourceDataId);
                w.WriteString("Type", p.Kind.ToString());
                break;
            case InlineFile i:
                w.WriteString("SourceDataID", i.SourceDataId);
                break;
            case ArchiveMeta m:
                w.WriteString("SourceDataID", m.SourceDataId);
                break;
            case CreateBsa b:
                w.WriteString("TempID", Path(b.TempId));
                w.WritePropertyName("State");
                b.State.WriteTo(w);
                w.WriteStartArray("FileStates");
                foreach (var fileState in b.FileStates) fileState.WriteTo(w);
                w.WriteEndArray();
                break;
            case MergedPatch m:
                w.WriteString("PatchID", m.PatchId);
                w.WriteStartArray("Sources");
                foreach (var source in m.Sources)
                {
                    w.WriteStartObject();
                    w.WriteString("Hash", WabbajackHash.ToBase64(source.XxHash64));
                    w.WriteString("RelativePath", Path(source.Path));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                break;
        }
        w.WriteEndObject();
    }
}
