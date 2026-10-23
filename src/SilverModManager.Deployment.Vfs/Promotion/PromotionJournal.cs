using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Deployment.Vfs;

public enum JournalEntryKind { File, Whiteout }

/// <summary>One staged change. Whiteouts have no content, so their size, hash and mode are zero.</summary>
public readonly record struct JournalEntry(RelativePath Path, JournalEntryKind Kind, Size Size, Hash Hash, uint Mode);

/// <summary>
/// What a promotion is about to do, written down before anything is stored so a crash can be
/// replayed. It lives in the staging location next to the staging directory, not inside it:
/// emptying the staging directory must not delete the only record of what was in it.
/// </summary>
/// <param name="Recorded">Set once the loadout has the changes; from then on only the cleanup is left.</param>
public sealed record PromotionJournal(GameInstallMetadataId Installation, LoadoutId Target, IReadOnlyList<JournalEntry> Entries, bool Recorded = false)
{
    public const int CurrentVersion = 1;

    public PromotionJournal With(bool recorded) => this with { Recorded = recorded };

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Writes the journal beside the old one and renames it over, after flushing it to disk, so a
    /// crash leaves one complete journal or the other.
    /// </summary>
    public void Write(AbsolutePath file)
    {
        var dto = new JournalDto
        {
            Version = CurrentVersion,
            Installation = Installation.Value.Value.ToString("x", CultureInfo.InvariantCulture),
            Target = Target.Value.Value.ToString("x", CultureInfo.InvariantCulture),
            Recorded = Recorded,
            Entries = Entries.Select(e => new EntryDto
            {
                Path = e.Path.ToString(),
                Kind = e.Kind == JournalEntryKind.File ? "file" : "whiteout",
                Size = e.Kind == JournalEntryKind.File ? e.Size.Value : null,
                XxHash3 = e.Kind == JournalEntryKind.File ? ((ulong)e.Hash).ToString("x16", CultureInfo.InvariantCulture) : null,
                Mode = e.Kind == JournalEntryKind.File ? e.Mode : null,
            }).ToList(),
        };

        file.Parent.CreateDirectory();
        var temp = file.Parent.Combine(file.FileName + ".tmp");
        using (var stream = new FileStream(temp.ToString(), FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, dto, Options);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp.ToString(), file.ToString(), overwrite: true);
    }

    /// <exception cref="InvalidDataException">The file is not a journal this build understands, or belongs to another installation.</exception>
    public static PromotionJournal Read(AbsolutePath file, GameInstallMetadataId expectedInstallation)
    {
        JournalDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<JournalDto>(File.ReadAllBytes(file.ToString()), Options);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"Promotion journal {file} is not valid JSON", e);
        }

        if (dto is null) throw new InvalidDataException($"Promotion journal {file} is empty");
        if (dto.Version != CurrentVersion) throw new InvalidDataException($"Promotion journal {file} has version {dto.Version}, this build reads version {CurrentVersion}");

        try
        {
            var installation = GameInstallMetadataId.From(ulong.Parse(dto.Installation ?? "", NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            if (installation != expectedInstallation)
                throw new InvalidDataException($"Promotion journal {file} belongs to installation {dto.Installation}, not {expectedInstallation.Value.Value:x}");

            var entries = (dto.Entries ?? throw new InvalidDataException($"Promotion journal {file} has no entry list")).Select(e => e.Kind switch
            {
                "file" => new JournalEntry(
                    RelativePath.FromUnsanitizedInput(e.Path!),
                    JournalEntryKind.File,
                    Size.From(e.Size!.Value),
                    Hash.From(ulong.Parse(e.XxHash3!, NumberStyles.HexNumber, CultureInfo.InvariantCulture)),
                    e.Mode!.Value),
                "whiteout" => new JournalEntry(RelativePath.FromUnsanitizedInput(e.Path!), JournalEntryKind.Whiteout, Size.Zero, Hash.From(0UL), 0),
                _ => throw new InvalidDataException($"Promotion journal {file} has an entry of unknown kind '{e.Kind}'"),
            }).ToArray();

            return new PromotionJournal(
                installation,
                LoadoutId.From(ulong.Parse(dto.Target ?? "", NumberStyles.HexNumber, CultureInfo.InvariantCulture)),
                entries,
                dto.Recorded);
        }
        catch (Exception e) when (e is FormatException or OverflowException or NullReferenceException or InvalidOperationException)
        {
            throw new InvalidDataException($"Promotion journal {file} has a malformed entry", e);
        }
    }

    private sealed class JournalDto
    {
        public int Version { get; set; }
        public string? Installation { get; set; }
        public string? Target { get; set; }
        public bool Recorded { get; set; }
        public List<EntryDto>? Entries { get; set; }
    }

    private sealed class EntryDto
    {
        public string? Path { get; set; }
        public string? Kind { get; set; }
        public ulong? Size { get; set; }
        public string? XxHash3 { get; set; }
        public uint? Mode { get; set; }
    }
}
