using System.Collections.Immutable;

namespace SilverModManager.Games.Generic.ModProvenance;

/// <summary>How a mod was identified. The order is the priority: an earlier method wins over a later one.</summary>
public enum ClaimMethod
{
    /// <summary>Another mod manager's own records of what it installed.</summary>
    ManagerRecords,

    /// <summary>A lock file a manager wrote next to the game.</summary>
    LockFile,

    /// <summary>An archive on disk whose MD5 Nexus Mods knows.</summary>
    Archive,

    /// <summary>The folder's name matched a mod page, and the page's file listing matched the folder's files.</summary>
    NameChecked,
}

public enum ClaimSource
{
    Nexus,
    Thunderstore,
}

/// <summary>One statement about where a mod came from.</summary>
/// <param name="Mod">What the source calls the mod (a folder or table name).</param>
/// <param name="Origin">Who said so, for the user: "Fluorine", "Mod Organizer 2", "r2modman", "erm".</param>
/// <param name="ModId">The Nexus Mods page, if known.</param>
/// <param name="FileId">The Nexus Mods file, if known.</param>
/// <param name="Archive">The name of the archive it was installed from, if known.</param>
/// <param name="Package">The Thunderstore package (<c>Owner-Name</c>).</param>
public sealed record ModClaim(
    string Mod,
    ClaimMethod Method,
    string Origin,
    ClaimSource Source,
    uint? ModId = null,
    uint? FileId = null,
    string? Version = null,
    string? Archive = null,
    string? Package = null)
{
    /// <summary>How the confirm list says the mod was identified.</summary>
    public string How => Method switch
    {
        ClaimMethod.ManagerRecords => $"from {Origin}'s records",
        ClaimMethod.LockFile => $"from {Origin}'s lock file",
        ClaimMethod.Archive => "matched its archive",
        _ => "by name, files checked",
    };

    /// <summary>The ids the claim names, for a sentence.</summary>
    public string Describe() => Source == ClaimSource.Thunderstore
        ? $"package {Package} {Version}".TrimEnd()
        : (ModId, FileId) switch
        {
            ({ } mod, { } file) => $"file {file} of mod {mod}",
            ({ } mod, null) => $"mod {mod}",
            (null, { } file) => $"file {file}",
            _ => "no ids",
        };
}

/// <summary>The game a cleanup is about, as far as matching other managers' records goes.</summary>
/// <param name="GameFolder">The folder a manager's <c>gamePath</c> is compared with.</param>
/// <param name="Names">Names the game goes by in other managers ("Elden Ring"); compared ignoring case and punctuation.</param>
/// <param name="NexusGameId">The numeric Nexus Mods game id; archives are looked up only when it is known.</param>
public sealed record GameIdentity(string GameFolder, IReadOnlyList<string> Names, uint? NexusGameId = null);

/// <summary>Where to look for other managers. Every path comes from the caller, so a test never reads the real home.</summary>
/// <param name="Home">The home folder: Flatpak data, <c>~/.config</c>, <c>~/.local/share</c> and <c>~/Downloads</c> hang off it.</param>
/// <param name="ExtraRoots">More places to search for manager installs, such as a Wine prefix.</param>
/// <param name="ExtraArchiveFolders">More folders to look for archives in, such as erm's <c>vendor</c> folder.</param>
public sealed record ManagerSearch(string Home, IReadOnlyList<string> ExtraRoots, IReadOnlyList<string> ExtraArchiveFolders)
{
    /// <summary>Looks nowhere: no home folder and no extra places.</summary>
    public static ManagerSearch None { get; } = new("", [], []);
}

/// <param name="Claims">What the managers of this game say about their mods.</param>
/// <param name="ArchiveFolders">Download folders those managers keep, absolute.</param>
/// <param name="Problems">What was skipped or could only be partly read, one sentence each.</param>
public sealed record ManagerRecordResult(ImmutableArray<ModClaim> Claims, ImmutableArray<string> ArchiveFolders, ImmutableArray<string> Problems);

/// <param name="Md5">Lowercase hex.</param>
/// <param name="FileName">The archive's name on Nexus Mods.</param>
public sealed record ArchiveHit(string Md5, string FileName, uint GameId, uint ModId, uint FileId);

/// <summary>A file listed inside an uploaded archive.</summary>
public sealed record ArchiveEntry(string Path, long Size);

/// <summary>What the identifier asks Nexus Mods. The app implements it over its clients; tests answer from memory.</summary>
public interface IArchiveCatalog
{
    /// <summary>The uploaded archives among <paramref name="md5s"/>. Nexus Mods knows whole archives only, never the files inside them.</summary>
    Task<IReadOnlyList<ArchiveHit>> LookupArchivesAsync(IReadOnlyList<string> md5s, CancellationToken cancellationToken);

    /// <summary>The files inside an uploaded archive, or null when no listing is available.</summary>
    Task<IReadOnlyList<ArchiveEntry>?> ListArchiveContentAsync(uint game, uint modId, uint fileId, CancellationToken cancellationToken);
}

/// <param name="Mods">The names of the mods to identify, typically folders about to be deleted.</param>
/// <param name="LockClaims">Claims read from a lock file, which the identifier cannot find itself.</param>
public sealed record IdentifyRequest(GameIdentity Game, IReadOnlyList<string> Mods, ManagerSearch Search, IReadOnlyList<ModClaim> LockClaims);

/// <param name="Chosen">The highest-priority claim, or null when nothing identified the mod.</param>
/// <param name="Disagreements">Sentences for each lower-priority claim that names something else.</param>
public sealed record ModIdentification(string Mod, ModClaim? Chosen, ImmutableArray<string> Disagreements);

public sealed record IdentifyResult(ImmutableArray<ModIdentification> Mods, ImmutableArray<string> Problems);
