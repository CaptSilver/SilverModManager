using System.Collections.Immutable;
using Tomlyn;
using Tomlyn.Model;

namespace SilverModManager.Games.FromSoft.Cleanup.SaveToLibrary;

public static class ProvenanceSources
{
    public const string Nexus = "nexus";
    public const string GitHub = "github";
}

/// <summary>One mod a lock file says another manager installed.</summary>
/// <param name="Id">The mod's table name in the lock.</param>
/// <param name="Source">Where the mod came from, <see cref="ProvenanceSources"/>.</param>
/// <param name="Version">The pinned version, if the lock gave one.</param>
/// <param name="FileId">The Nexus Mods file id, if the lock gave a usable one.</param>
/// <param name="Asset">The archive's file name, if the lock gave one.</param>
/// <param name="ModId">The Nexus Mods page, when a source other than the lock's archive name gave it.</param>
public sealed record ProvenanceEntry(string Id, string Source, string? Version, uint? FileId, string? Asset, uint? ModId = null);

/// <param name="Entries">Every entry that could be read, in the file's order.</param>
/// <param name="Problems">What was skipped or refused, one sentence each.</param>
public sealed record ProvenanceList(ImmutableArray<ProvenanceEntry> Entries, ImmutableArray<string> Problems)
{
    public static ProvenanceList Empty { get; } = new([], []);
}

/// <summary>
/// Reads the <c>mods.lock.toml</c> another mod manager keeps: one table per mod with <c>version</c>, <c>file_id</c>,
/// <c>asset</c>, <c>sha256</c> and <c>source</c>. The file is untrusted input, so unlike the strict importer this keeps
/// every entry it can make sense of and says what it dropped; it never throws on what the file contains.
/// </summary>
public static class ProvenanceReader
{
    /// <summary>A lock is a few kilobytes; anything bigger than this is not one.</summary>
    public const int MaxChars = 1_048_576;

    private const int MaxEntries = 5000;
    private const int MaxValueChars = 1024;

    public static ProvenanceList Read(string text)
    {
        if (text.Length > MaxChars) return Refused($"The lock file is too large to read ({text.Length} characters, the limit is {MaxChars}).");
        if (string.IsNullOrWhiteSpace(text)) return ProvenanceList.Empty;

        TomlTable root;
        try
        {
            root = TomlSerializer.Deserialize<TomlTable>(text) ?? throw new InvalidDataException("The lock file is empty.");
        }
        catch (Exception e) when (e is TomlException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            return Refused($"The lock file is not valid TOML: {e.Message}");
        }

        var entries = ImmutableArray.CreateBuilder<ProvenanceEntry>();
        var problems = ImmutableArray.CreateBuilder<string>();
        foreach (var (id, value) in root)
        {
            if (entries.Count >= MaxEntries)
            {
                problems.Add($"The lock file lists more than {MaxEntries} mods; the rest were not read.");
                break;
            }

            if (value is not TomlTable table)
            {
                problems.Add($"'{Shown(id)}' is not a table of one mod and was skipped.");
                continue;
            }

            if (Text(table, "source") is not { } source)
            {
                problems.Add($"'{Shown(id)}' names no source and was skipped.");
                continue;
            }

            var entry = new ProvenanceEntry(id, source.ToLowerInvariant(), Text(table, "version"), FileId(table), Text(table, "asset"));
            foreach (var (key, usable) in new[] { ("version", entry.Version is not null), ("asset", entry.Asset is not null), ("file_id", entry.FileId is not null) })
            {
                if (table.ContainsKey(key) && !usable) problems.Add($"'{Shown(id)}' has a {key} that is not usable and it was ignored.");
            }

            entries.Add(entry);
        }

        return new ProvenanceList(entries.ToImmutable(), problems.ToImmutable());
    }

    private static ProvenanceList Refused(string problem) => new([], [problem]);

    private static string? Text(TomlTable table, string key)
        => table.TryGetValue(key, out var value) && value is string { Length: > 0 and <= MaxValueChars } text ? text : null;

    private static uint? FileId(TomlTable table)
        => Text(table, "file_id") is { } text && uint.TryParse(text, System.Globalization.NumberStyles.None, null, out var id) && id != 0 ? id : null;

    // Names come from the file, so keep what reaches a log or a dialog short.
    private static string Shown(string name) => name.Length <= 60 ? name : name[..60] + "...";
}
