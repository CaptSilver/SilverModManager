using System.Collections.Concurrent;
using SilverModManager.Games.FromSoft.Formats;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>A regulation inside a library archive that carries the build stamp being looked for.</summary>
/// <param name="ArchiveName">The archive's file name.</param>
/// <param name="Member">The file's path inside the archive.</param>
/// <param name="Hash">Its hash in the file store.</param>
public sealed record ArchiveRegulation(string ArchiveName, string Member, Hash Hash)
{
    /// <summary>The file sits in a folder called Vanilla. Only used to put likelier candidates first; a name proves nothing.</summary>
    public bool InVanillaFolder => Member.Split('/')[..^1].Any(p => p.Equals("Vanilla", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Finds regulations of one build stamp among everything the library's archives hold. A mod author often bundles the
/// unmodified file the mod was made from, but nothing here relies on that: every regulation in the library is a
/// candidate, and whether it can be an ancestor is decided by the checks of <see cref="Heal.VanillaValidation"/>.
/// <para>
/// Reads the library through model accessors only, like <see cref="VanillaDumps"/>.
/// </para>
/// </summary>
public sealed class ArchiveVanillas(IFileStore fileStore)
{
    // Reading a stamp decrypts the whole regulation, so it is remembered: the same file turns up in every plan. The
    // game's key is part of the key here, since two games share the file name and a miss under one key says nothing
    // about the other.
    private readonly ConcurrentDictionary<(Hash Hash, string Key), string?> _versions = new();

    /// <summary>
    /// Every regulation held by a library archive whose stamp is <paramref name="version"/>, one per distinct file,
    /// likeliest first, apart from <paramref name="except"/> (the contributors' own files, which are what the mods
    /// changed and so never their vanilla).
    /// </summary>
    public IReadOnlyList<ArchiveRegulation> Find(IDb db, FromSoftFormatTraits traits, string version, ISet<Hash> except)
    {
        if (traits.RegulationFileName is not { } regulationName) return [];

        var found = new Dictionary<Hash, ArchiveRegulation>();
        foreach (var entry in LibraryArchiveFileEntry.All(db).OrderBy(e => e.Id.Value))
        {
            var path = entry.Path.ToString();
            if (!VanillaDump.FileName(path).Equals(regulationName, StringComparison.OrdinalIgnoreCase)) continue;

            var hash = entry.AsLibraryFile().Hash;
            if (except.Contains(hash) || found.ContainsKey(hash)) continue;
            if (!VersionOf(hash, traits).Equals(version, StringComparison.Ordinal)) continue;

            found[hash] = new ArchiveRegulation(entry.Parent.AsLibraryFile().FileName.ToString(), path, hash);
        }

        return found.Values.OrderByDescending(r => r.InVanillaFolder).ToArray();
    }

    /// <summary>The bytes of a file the library holds.</summary>
    public byte[] Read(Hash hash)
    {
        using var stream = fileStore.GetFileStream(hash).GetAwaiter().GetResult();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    // The empty string stands for a file that is not a regulation this game's key opens; it matches no stamp.
    private string VersionOf(Hash hash, FromSoftFormatTraits traits)
        => _versions.GetOrAdd((hash, Convert.ToHexString(traits.RegulationKey.AsSpan())), k =>
        {
            try
            {
                return Regulation.Version(Regulation.Unpack(Read(k.Hash), traits));
            }
            catch (Exception exception) when (exception is RegulationException or AesException or DcxException or KrakenException)
            {
                return null;
            }
        }) ?? "";
}
