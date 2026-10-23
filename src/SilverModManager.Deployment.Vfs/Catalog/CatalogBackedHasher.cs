using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Deployment.Vfs;

/// <summary>
/// Hashes a file unless the catalog already holds a hash recorded under the file's current
/// fingerprint.
/// </summary>
public sealed class CatalogBackedHasher : IFileHasher
{
    private readonly IVfsCatalog _catalog;
    private readonly IBackingScanner _scanner;
    private readonly IConnection _connection;
    private readonly IFileHasher _inner;

    public CatalogBackedHasher(IVfsCatalog catalog, IBackingScanner scanner, IConnection connection)
        : this(catalog, scanner, connection, new XxHash3FileHasher()) { }

    // Not public so the container only sees the constructor above: this type is itself the registered
    // IFileHasher, and asking the container for another one would be a cycle.
    internal CatalogBackedHasher(IVfsCatalog catalog, IBackingScanner scanner, IConnection connection, IFileHasher inner)
    {
        _catalog = catalog;
        _scanner = scanner;
        _connection = connection;
        _inner = inner;
    }

    /// <exception cref="FileNotFoundException">The file is not there to take a fingerprint of.</exception>
    public async ValueTask<Hash> Hash(GameInstallMetadataId installation, AbsolutePath file, CancellationToken token = default)
    {
        var entry = _scanner.Stat(file) ?? throw new FileNotFoundException($"Cannot fingerprint `{file}`: it does not exist", file.ToString());
        var fingerprint = FileFingerprint.From(entry);
        if (_catalog.TryGetHash(installation, file, fingerprint, out var cached)) return cached;

        var hash = await _inner.Hash(installation, file, token);

        // The file may have changed while it was being read. The fingerprint taken first then no
        // longer matches the file, so the row it produces is never served.
        using var tx = _connection.BeginTransaction();
        _catalog.Record(tx, installation, file, fingerprint, hash);
        await tx.Commit();
        return hash;
    }
}
