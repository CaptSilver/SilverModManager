using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Heal;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>
/// A folder of a library archive that holds an unmodified copy of the game's regulation, and so the vanilla files
/// beside it.
/// </summary>
/// <param name="ArchiveId">The library archive.</param>
/// <param name="ArchiveName">The name of the archive's library item, for display.</param>
/// <param name="Root">The folder inside the archive that holds the regulation; empty for the archive's top.</param>
/// <param name="Version">The build stamp of the dump's regulation.</param>
/// <param name="Members">Every file under <paramref name="Root"/>, by its path below it.</param>
/// <param name="Validated">The regulation is a baseline the app kept after checking it, not one the game's own files vouch for.</param>
public sealed record VanillaDump(EntityId ArchiveId, string ArchiveName, string Root, string Version, IReadOnlyDictionary<string, Hash> Members, bool Validated = false)
{
    /// <summary>
    /// The member that stands for the package-relative <paramref name="path"/>: the one at that path below the root,
    /// otherwise the one with that file name if it is the only one in the dump. A dump bundled by a mod author rarely
    /// mirrors the game's folder layout (<c>Vanilla/m00.talkesdbnd.dcx</c> for <c>script/talk/m00.talkesdbnd.dcx</c>).
    /// </summary>
    public bool TryResolve(string path, out string member, out Hash hash)
    {
        if (Members.TryGetValue(path, out hash))
        {
            member = path;
            return true;
        }

        var name = FileName(path);
        var same = Members.Where(m => FileName(m.Key).Equals(name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (same.Length == 1)
        {
            (member, hash) = (same[0].Key, same[0].Value);
            return true;
        }

        (member, hash) = (path, default);
        return false;
    }

    internal static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];
}

/// <summary>A folder that looks like a vanilla dump but whose regulation the app cannot vouch for.</summary>
/// <param name="Members">Every file under <paramref name="Root"/>, by its path below it.</param>
public sealed record UnconfirmedDump(string ArchiveName, string Root, string RegulationName, string Version, IReadOnlyDictionary<string, Hash> Members)
{
    /// <summary>Whether the folder has a copy of the package-relative <paramref name="path"/>, found the way <see cref="VanillaDump.TryResolve"/> finds one.</summary>
    public bool HoldsCopyOf(string path) => new VanillaDump(default, ArchiveName, Root, Version, Members).TryResolve(path, out _, out _);

    /// <summary>Says why the copy of <paramref name="path"/> in this folder was not used as the ancestor.</summary>
    public string Describe(string path)
        => $"archive {ArchiveName} holds a {RegulationName} of version {Version} the app cannot confirm as vanilla, so its copy of {path} was not used";
}

/// <summary>
/// Finds the vanilla dumps in the library: folders of an archive holding a regulation the app can vouch for (see
/// <see cref="IVouchedRegulations"/>), or one it kept as a validated baseline from that very archive. The vanilla copy
/// of a packed file is a sibling of that regulation, so the ancestor of a three-way merge is found without anyone
/// declaring where it is.
/// <para>
/// Re-entrancy: this runs while the merge index is built, inside the winning-files query, so it reads the library
/// through model accessors only (<c>FindByHash</c>, <c>Children</c>) and never through a database query.
/// </para>
/// </summary>
public sealed class VanillaDumps(IFileStore fileStore, IVouchedRegulations vouched, RegulationBaselines baselines, ILogger<VanillaDumps> logger)
{
    private readonly ConcurrentDictionary<Hash, string?> _versions = new();

    /// <summary>Every recognised dump, in a stable order.</summary>
    public IReadOnlyList<VanillaDump> Find(IDb db, GameInstallation installation)
    {
        var traits = TraitsOf(installation);
        if (traits.RegulationFileName is not { } regulationName) return [];

        var found = new Dictionary<(EntityId Archive, string Root), (LibraryArchive.ReadOnly Archive, Hash Regulation, bool Validated)>();
        foreach (var hash in vouched.Hashes(installation))
        {
            foreach (var entry in RegulationEntries(db, hash, regulationName))
                found.TryAdd((entry.Parent.Id, RootOf(entry.Path.ToString())), (entry.Parent, hash, false));
        }

        // Added after the vouched ones, so a regulation that is both stays a vouched dump.
        foreach (var (hash, origin) in baselines.ValidatedFromArchives())
        {
            foreach (var entry in RegulationEntries(db, hash, regulationName).Where(e => ValidatedFrom(origin, e.Parent)))
                found.TryAdd((entry.Parent.Id, RootOf(entry.Path.ToString())), (entry.Parent, hash, true));
        }

        var dumps = new List<VanillaDump>();
        foreach (var ((archiveId, root), (archive, regulation, validated)) in found.OrderBy(f => f.Key.Archive.Value).ThenBy(f => f.Key.Root, StringComparer.Ordinal))
        {
            if (VersionOf(regulation, traits) is not { } version)
            {
                logger.LogWarning("{Archive} holds a {Regulation} the app vouches for but cannot read, so it is not used as a vanilla dump", DisplayName(archive), regulationName);
                continue;
            }

            dumps.Add(new VanillaDump(archiveId, DisplayName(archive), root, version, MembersUnder(archive, root), validated));
        }

        return dumps;
    }

    /// <summary>Folders named like a vanilla dump whose regulation the app cannot vouch for, so a report can say why they were not used.</summary>
    public IReadOnlyList<UnconfirmedDump> Unconfirmed(IDb db, GameInstallation installation)
    {
        var traits = TraitsOf(installation);
        if (traits.RegulationFileName is not { } regulationName) return [];

        var known = vouched.Hashes(installation);
        var validated = baselines.ValidatedFromArchives();
        var result = new List<UnconfirmedDump>();
        foreach (var entry in LibraryArchiveFileEntry.All(db).OrderBy(e => e.Id.Value))
        {
            var path = entry.Path.ToString();
            if (!VanillaDump.FileName(path).Equals(regulationName, StringComparison.OrdinalIgnoreCase)) continue;
            if (known.Contains(entry.AsLibraryFile().Hash)) continue;
            if (validated.Any(v => v.Hash == entry.AsLibraryFile().Hash && ValidatedFrom(v.Origin, entry.Parent))) continue;

            // Every mod ships its own regulation, so only a folder called Vanilla is taken for someone's attempt at a dump.
            var root = RootOf(path);
            if (!VanillaDump.FileName(root).Equals("Vanilla", StringComparison.OrdinalIgnoreCase)) continue;
            if (VersionOf(entry.AsLibraryFile().Hash, traits) is not { } version) continue;

            result.Add(new UnconfirmedDump(DisplayName(entry.Parent), root, regulationName, version, MembersUnder(entry.Parent, root)));
        }

        return result;
    }

    /// <summary>
    /// Which of <paramref name="wanted"/> some recognised dump offers a copy of, in the spelling it was asked in. This
    /// is what the merge index is given as the paths with a vanilla source.
    /// </summary>
    public IReadOnlySet<string> Paths(IDb db, GameInstallation installation, IEnumerable<string> wanted)
    {
        var offered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dumps = Find(db, installation);
        if (dumps.Count == 0) return offered;

        foreach (var path in wanted)
        {
            if (dumps.Any(d => d.TryResolve(path, out _, out _))) offered.Add(path);
        }
        return offered;
    }

    /// <summary>The bytes of the member standing for <paramref name="path"/>, from the file store.</summary>
    /// <exception cref="MergeException">The dump has no such member.</exception>
    public byte[] Member(VanillaDump dump, string path)
    {
        if (!dump.TryResolve(path, out _, out var hash))
            throw new MergeException($"archive {dump.ArchiveName} holds no vanilla copy of {path}");
        return ReadBytes(hash);
    }

    /// <summary>
    /// The name the user knows the archive by. The library file keeps the name it was stored under, which for a
    /// download is a temporary GUID name; the library item carries the readable one.
    /// </summary>
    internal static string DisplayName(LibraryArchive.ReadOnly archive) => archive.AsLibraryFile().AsLibraryItem().Name;

    // The regulation files of the library that have this content, as archive entries; a hash can also belong to a loose file.
    private static IEnumerable<LibraryArchiveFileEntry.ReadOnly> RegulationEntries(IDb db, Hash hash, string regulationName)
    {
        foreach (var file in LibraryFile.FindByHash(db, hash))
        {
            var entry = LibraryArchiveFileEntry.Load(db, file.Id);
            if (entry.IsValid() && VanillaDump.FileName(entry.Path.ToString()).Equals(regulationName, StringComparison.OrdinalIgnoreCase))
                yield return entry;
        }
    }

    // A validated baseline records "archive name: member". Only the archive it was checked in may borrow that trust:
    // another archive may hold the same regulation next to files nobody looked at.
    private static bool ValidatedFrom(string origin, LibraryArchive.ReadOnly archive)
        => origin.StartsWith(DisplayName(archive) + ": ", StringComparison.Ordinal);

    private static string RootOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    private static Dictionary<string, Hash> MembersUnder(LibraryArchive.ReadOnly archive, string root)
    {
        var prefix = root.Length == 0 ? "" : root + "/";
        var members = new Dictionary<string, Hash>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in archive.Children)
        {
            var path = child.Path.ToString();
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) members[path[prefix.Length..]] = child.AsLibraryFile().Hash;
        }
        return members;
    }

    private string? VersionOf(Hash regulation, FromSoftFormatTraits traits)
    {
        if (_versions.TryGetValue(regulation, out var known)) return known;

        string? version;
        try
        {
            version = Regulation.Version(Regulation.Unpack(ReadBytes(regulation), traits));
        }
        catch (Exception exception) when (exception is RegulationException or AesException or DcxException or KrakenException)
        {
            version = null;
        }

        _versions[regulation] = version;
        return version;
    }

    private byte[] ReadBytes(Hash hash)
    {
        using var stream = fileStore.GetFileStream(hash).GetAwaiter().GetResult();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static FromSoftFormatTraits TraitsOf(GameInstallation installation)
        => (installation.Game as IFromSoftGame
            ?? throw new InvalidOperationException($"{installation.Game.DisplayName} is not a FromSoft game, so it has no vanilla dumps.")).Formats;
}
