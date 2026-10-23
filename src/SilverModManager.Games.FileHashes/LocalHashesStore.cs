using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.MnemonicDB;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Storage;
using SilverModManager.MnemonicDB.Storage.RocksDbBackend;
using NexusMods.Paths;

namespace SilverModManager.Games.FileHashes;

/// <summary>
/// A writable MnemonicDB store registered with the query engine under a name. Its main use is the
/// store of rows this app builds from the game's own install data: unlike the downloaded databases it
/// is never replaced, and the query engine knows it as <see cref="ConnectionName"/>. The same type
/// backs <see cref="OpenEmptyPlaceholder"/>.
/// </summary>
internal sealed class LocalHashesStore : IDisposable
{
    /// <summary>
    /// The name SQL addresses this store by: <c>MDB_HashRelation(DbName=>'local_hashes')</c>.
    /// </summary>
    public const string ConnectionName = "local_hashes";

    private readonly Backend _backend;
    private readonly DatomStore _store;

    public LocalHashesStore(IServiceProvider provider, IQueryEngine queryEngine, FileHashesServiceSettings settings, IFileSystem fileSystem)
        : this(provider, queryEngine, ConnectionName, StoreSettings(settings, fileSystem))
    {
    }

    /// <summary>
    /// An empty in-memory store registered as <paramref name="name"/>, nothing ever written to it. SQL
    /// that names a store the query engine does not know fails to bind, which would take every query
    /// down with it while no downloaded database is connected. The store has to open writable because
    /// opening a fresh store read-only skips the migration that creates the attribute schema, and a
    /// table function over an unknown attribute fails as well.
    /// </summary>
    public static LocalHashesStore OpenEmptyPlaceholder(IServiceProvider provider, IQueryEngine queryEngine, string name)
        => new(provider, queryEngine, name, DatomStoreSettings.InMemory);

    private LocalHashesStore(IServiceProvider provider, IQueryEngine queryEngine, string name, DatomStoreSettings storeSettings)
    {
        // The downloaded databases open read-only. These open writable, the placeholder included, so they need the default backend.
        _backend = new Backend();

        try
        {
            _store = new DatomStore(provider.GetRequiredService<ILogger<DatomStore>>(), storeSettings, _backend);
            Connection = new Connection(provider.GetRequiredService<ILogger<Connection>>(), _store, provider, [], readOnlyMode: false, prefix: name, queryEngine: queryEngine);
        }
        catch
        {
            _store?.Dispose();
            _backend.Dispose();
            throw;
        }
    }

    private static DatomStoreSettings StoreSettings(FileHashesServiceSettings settings, IFileSystem fileSystem)
    {
        if (settings.UseInMemoryLocalBaseline) return DatomStoreSettings.InMemory;

        var path = settings.LocalBaselineLocation.ToPath(fileSystem);
        path.CreateDirectory();
        return new DatomStoreSettings { Path = path };
    }

    public IConnection Connection { get; }

    public void Dispose()
    {
        // Disposing the connection is what unregisters its name from the query engine.
        ((Connection)Connection).Dispose();
        _store.Dispose();
        _backend.Dispose();
    }
}
